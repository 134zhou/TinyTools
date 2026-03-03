#include "pch.h"
#define SPECTRUM_EXPORTS // 必须与头文件中的 #ifdef 名字完全一致
#include "SpectrumProcess.h"

#include <Eigen/Sparse>
#include <Eigen/Dense>
#include <vector>
#include <iostream>
#include <cmath>
#include <Eigen/Dense>
#include <unsupported/Eigen/NonLinearOptimization>

using namespace Eigen;

extern "C"
{
    SPECTRUM_API void RemoveBaseline(double* y_in, double* y_out, double* baseline, int n, double lambda, double p)
    {
        if (n < 3 || y_in == nullptr || y_out == nullptr) return;

        // 1. 构建二阶差分矩阵 D
        std::vector<Triplet<double>> triplets;
        triplets.reserve(3 * (n - 2));
        for (int i = 0; i < n - 2; ++i)
        {
            triplets.push_back(Triplet<double>(i, i, 1.0));
            triplets.push_back(Triplet<double>(i, i + 1, -2.0));
            triplets.push_back(Triplet<double>(i, i + 2, 1.0));
        }
        SparseMatrix<double> D(n - 2, n);
        D.setFromTriplets(triplets.begin(), triplets.end());

        // 提前计算 DTD 并乘以 lambda，减少循环内运算
        SparseMatrix<double> lamDTD = lambda * (D.transpose() * D);

        VectorXd y = Map<VectorXd>(y_in, n);
        VectorXd w = VectorXd::Ones(n);
        VectorXd z(n);

        SparseLU<SparseMatrix<double>> solver;

        for (int iter = 0; iter < 10; ++iter)
        {
            // 构建权重对角矩阵 W (使用简洁构造方式)
            SparseMatrix<double> W(n, n);
            std::vector<Triplet<double>> w_triplets;
            w_triplets.reserve(n);
            for (int i = 0; i < n; ++i) w_triplets.push_back(Triplet<double>(i, i, w(i)));
            W.setFromTriplets(w_triplets.begin(), w_triplets.end());

            // 求解 (W + lambda * DTD) * z = W * y
            SparseMatrix<double> A = W + lamDTD;
            solver.compute(A);
            if (solver.info() != Success) return; // 鲁棒性检查

            z = solver.solve(W * y);

            // 更新权重
            for (int i = 0; i < n; ++i)
            {
                w(i) = (y(i) > z(i)) ? p : (1.0 - p);
            }
        }

        // 3. 将结果写回输出数组
        for (int i = 0; i < n; ++i)
        {
            y_out[i] = y(i) - z[i];
			baseline[i] = z[i];
        }
    }
}

// 1. 定义多峰累加的拟合数据结构
struct MultiPeakData
{
    const double* x;
    const double* y;
    int nPoints;      // 数据点总数
    int nPeaks;       // 峰的总数
    int fitType;      // 0: Gaussian, 1: Lorentzian
};

// 2. 定义多峰仿函数 (Functor)
struct MultiPeakFunctor
{
    MultiPeakData data;
    int mode; // 0: 全参数拟合 (3*N), 1: 仅高度拟合 (1*N)
    // 固定的位置和宽度，仅在 mode=1 时使用
    Eigen::VectorXd fixed_centers;
    Eigen::VectorXd fixed_widths;

    typedef double Scalar;
    enum
    {
        InputsAtCompileTime = Eigen::Dynamic,
        ValuesAtCompileTime = Eigen::Dynamic
    };
    typedef Eigen::VectorXd InputType;
    typedef Eigen::VectorXd ValueType;
    typedef Eigen::Matrix<Scalar, ValuesAtCompileTime, InputsAtCompileTime> JacobianType;

    int inputs() const
    {
        return (mode == 1) ? data.nPeaks : 3 * data.nPeaks;
    }
    int values() const { return data.nPoints; }

    int operator()(const Eigen::VectorXd& b, Eigen::VectorXd& fvec) const
    {
        for (int i = 0; i < data.nPoints; ++i)
        {
            double xi = data.x[i];
            double yi = data.y[i];
            double total_model_y = 0;

            for (int p = 0; p < data.nPeaks; ++p)
            {
                double A, c, s;
                if (mode == 1)
                {
                    A = b[p]; // 仅高度是变量
                    c = fixed_centers[p];
                    s = fixed_widths[p];
                }
                else
                {
                    A = b[3 * p + 0];
                    c = b[3 * p + 1];
                    s = b[3 * p + 2];
                }

                if (data.fitType == 0)
                { 
                    // Gaussian
                    total_model_y += A * std::exp(-std::pow(xi - c, 2) / (2 * std::pow(s, 2)));
                }
                else
                {
                    // Lorentzian
                    total_model_y += A * (std::pow(s, 2) / (std::pow(xi - c, 2) + std::pow(s, 2)));
                }
            }
            fvec[i] = total_model_y - yi;
        }
        return 0;
    }
};

// 辅助函数：根据 X 坐标查找最近的数据点索引
int FindClosestIndex(double* x, int n, double targetX)
{
    auto it = std::lower_bound(x, x + n, targetX);
    int idx = static_cast<int>(std::distance(x, it));
    if (idx >= n) return n - 1;
    if (idx > 0 && std::abs(x[idx - 1] - targetX) < std::abs(x[idx] - targetX)) return idx - 1;
    return idx;
}

// 辅助函数：根据 FWHM 估计初始峰宽
double EstimateWidth(double* x, double* y, int n, int centerIndex, int fitType) {
    if (centerIndex < 0 || centerIndex >= n) return 1.0;

    double peakHeight = y[centerIndex];
    double targetHeight = peakHeight / 2.0;

    // 向左搜索
    int left = centerIndex;
    while (left > 0 && y[left] > targetHeight) left--;

    // 向右搜索
    int right = centerIndex;
    while (right < n - 1 && y[right] > targetHeight) right++;

    double fwhm = x[right] - x[left];
    if (fwhm <= 0) fwhm = (x[n - 1] - x[0]) * 0.01; // 保底方案

    if (fitType == 0) return fwhm / 2.355; // Gaussian sigma
    else return fwhm / 2.0;               // Lorentzian HWHM
}

extern "C"
{
    SPECTRUM_API void FitPeakData(double* xData, double* yData, int dataLen,
                                    double* centers, double* amplitudes, double* widths,
                                    int peakCount, int fitType)
    {
        // --- 0. 预处理：自动纠正初值 ---
        for (int p = 0; p < peakCount; p++)
        {
            int idx = FindClosestIndex(xData, dataLen, centers[p]);
            // 修正中心到局部最高点，并修正初始高度
            centers[p] = xData[idx];
            amplitudes[p] = yData[idx] > 0 ? yData[idx] : 1.0;
            // 自动估计宽度
            widths[p] = EstimateWidth(xData, yData, dataLen, idx, fitType);
        }

        // --- 第一阶段：固定位置和宽度，只求高度 ---
        MultiPeakFunctor stage1_functor;
        stage1_functor.mode = 1;
        stage1_functor.fixed_centers = Eigen::Map<Eigen::VectorXd>(centers, peakCount);
        stage1_functor.fixed_widths = Eigen::Map<Eigen::VectorXd>(widths, peakCount);
        stage1_functor.data = { xData, yData, dataLen, peakCount, fitType };

        Eigen::VectorXd b_heights(peakCount);
        for (int p = 0; p < peakCount; p++) b_heights[p] = amplitudes[p];

        Eigen::NumericalDiff<MultiPeakFunctor> numDiff1(stage1_functor);
        Eigen::LevenbergMarquardt<Eigen::NumericalDiff<MultiPeakFunctor>> lm1(numDiff1);
        lm1.minimize(b_heights); // 快速确定各个峰的大小

        // --- 第二阶段：全放开，精细拟合 ---
        MultiPeakFunctor stage2_functor;
        stage2_functor.mode = 0; // 全参数模式
        stage2_functor.data = stage1_functor.data;

        Eigen::VectorXd b_full(3 * peakCount);
        for (int p = 0; p < peakCount; p++) {
            b_full[3 * p + 0] = b_heights[p];   // 使用第一阶段求得的高度
            b_full[3 * p + 1] = centers[p];      // 使用原始位置
            b_full[3 * p + 2] = widths[p];       // 使用估计的宽度
        }

        Eigen::NumericalDiff<MultiPeakFunctor> numDiff2(stage2_functor);
        Eigen::LevenbergMarquardt<Eigen::NumericalDiff<MultiPeakFunctor>> lm2(numDiff2);

        // 限制第二阶段的最大迭代次数，防止跑偏太远
        lm2.parameters.maxfev = 1000;
        lm2.minimize(b_full);

        for (int p = 0; p < peakCount; p++)
        {
            amplitudes[p] = b_full[3 * p + 0];
            centers[p] = b_full[3 * p + 1];
            widths[p] = std::abs(b_full[3 * p + 2]);
        }
    }
}