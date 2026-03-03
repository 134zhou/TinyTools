#pragma once

// 定义导出宏
#ifdef SPECTRUM_EXPORTS
    #define SPECTRUM_API __declspec(dllexport)
#else
    #define SPECTRUM_API __declspec(dllimport)
#endif

extern "C"
{
    SPECTRUM_API void RemoveBaseline(double* y_in, double* y_out, double* baseline, int n, double lambda, double p);

    SPECTRUM_API void FitPeakData(double* xData, double* yData, int dataLen,
        double* centers, double* amplitudes, double* widths,
        int peakCount, int fitType);
}