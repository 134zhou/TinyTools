using ScottPlot;
using ScottPlot.Plottables;
using System.Collections.Generic;
using System.IO; // 必须添加
using System.Linq; // 必须添加
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PeakFinder
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // 在类中定义全局变量存储原始数据
        private double[] rawX;
        private double[] rawY;
        private bool isDataLoaded = false;
        // 存储当前的标记线
        private List<ScottPlot.Plottables.VerticalLine> peakMarkers = new List<ScottPlot.Plottables.VerticalLine>();
        // 存储当前正在编辑的标记线
        private ScottPlot.Plottables.VerticalLine? activeMarker;
        // 存储当前去基底后的 Y 数据，供拟合使用
        private double[] lastCorrectedY;

        private ScottPlot.Plottables.HorizontalSpan RangeSpan;
        // --- 三条主要曲线的全局引用 ---
        private ScottPlot.Plottables.Scatter pRaw;          // 原始数据线
        private ScottPlot.Plottables.Scatter pBaseline;     // 基线
        private ScottPlot.Plottables.Scatter pCorrected;    // 处理后数据线
        
        private List<ScottPlot.Plottables.Scatter> pFited = new List<ScottPlot.Plottables.Scatter>();// 拟合线

        public MainWindow()
        {
            InitializeComponent();

            // 配置主图
            DataCanvas.Plot.Legend.IsVisible = true;
            DataCanvas.Plot.Legend.Alignment = Alignment.UpperRight;
            DataCanvas.Plot.Legend.FontSize = 20;


            InitRangeSpan();
        }

        [DllImport("PeakFinderDLL.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void RemoveBaseline(double[] y_in, double[] y_out, double[] baseline, int n, double lambda, double p);

        [DllImport("PeakFinderDLL.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void FitPeakData(double[] xData, double[] yData, int dataLen,
                                     double[] centers, double[] amplitudes, double[] widths,
                                     int peakCount, int fitType);

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            Microsoft.Win32.OpenFileDialog openFileDialog = new Microsoft.Win32.OpenFileDialog();
            openFileDialog.Filter = "光谱数据 (*.txt;*.csv)|*.txt;*.csv|所有文件 (*.*)|*.*";

            if (openFileDialog.ShowDialog() == true)
            {
                try
                {
                    string filePath = openFileDialog.FileName;

                    // 1. 读取文件所有行
                    string[] lines = File.ReadAllLines(filePath);

                    List<double> xList = new List<double>();
                    List<double> yList = new List<double>();

                    // 2. 解析每一行
                    foreach (string line in lines)
                    {
                        // 跳过空行或注释行
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                        // 拆分字符串（支持空格、Tab、逗号）
                        string[] parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

                        if (parts.Length >= 2)
                        {
                            if (double.TryParse(parts[0], out double x) && double.TryParse(parts[1], out double y))
                            {
                                xList.Add(x);
                                yList.Add(y);
                            }
                        }
                    }

                    // 3. 绘图
                    if (xList.Count > 0)
                    {
                        rawX = xList.ToArray();
                        rawY = yList.ToArray();
                        isDataLoaded = true;

                        // 1. 计算 X 轴的数据物理极限
                        double xMin = rawX.Min();
                        double xMax = rawX.Max();
                        double yMin = rawY.Min();
                        double yMax = rawY.Max();

                        if (pRaw != null) DataCanvas.Plot.Remove(pRaw);
                       
                        pRaw = DataCanvas.Plot.Add.Scatter(rawX, rawY);
                        pRaw.LegendText = "Raw Data";
                        pRaw.Color = new ScottPlot.Color(0, 0, 0);
                        pRaw.LineWidth = 1;
                        pRaw.MarkerSize = 0;
                        
       
                        UpdateBaselineDisplay(); // 加载后立即显示

                        // 清除之前的旧规则
                        DataCanvas.Plot.Axes.Rules.Clear();
                        // 创建边界对象
                        // X轴限定在 [xMin, xMax] 之间
                        // Y轴限定在 [0, 1e10] 之间 (1e10 模拟正无穷)
                        var boundaryLimits = new AxisLimits(xMin, xMax, 0, yMax*2);

                        // 应用“最大边界”规则
                        // 它的逻辑是：如果视窗试图超出这个 Limits，强制将其弹回
                        var boundaryRule = new ScottPlot.AxisRules.MaximumBoundary(
                            DataCanvas.Plot.Axes.Bottom, // X 轴
                            DataCanvas.Plot.Axes.Left,   // Y 轴
                            boundaryLimits
                        );

                        DataCanvas.Plot.Axes.Rules.Add(boundaryRule);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"读取文件失败: {ex.Message}");
                }
            }

            ChkEnableRange.IsChecked = false; // 加载新数据时默认关闭范围选择
        }


        private void Save_Click(object sender, RoutedEventArgs e)
        {
            // 1. 检查是否有数据可保存
            if (!isDataLoaded || rawX == null)
            {
                MessageBox.Show("当前没有已处理的数据可以保存。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 2. 配置保存对话框
            Microsoft.Win32.SaveFileDialog saveFileDialog = new Microsoft.Win32.SaveFileDialog();
            saveFileDialog.Filter = "文本文件 (*.txt)|*.txt|逗号分隔文件 (*.csv)|*.csv";
            saveFileDialog.FileName = "Processed_Spectrum"; // 默认文件名
            saveFileDialog.DefaultExt = ".txt";

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    // 3. 执行去基底计算（确保保存的是当前滑块参数下的最新结果）
                    int n = rawY.Length;
                    double[] correctedY = new double[n];
                    double[] baselineY = new double[n];
                    double lambda = Math.Pow(10, SldLambda.Value);
                    double p = SldP.Value;

                    RemoveBaseline(rawY, correctedY, baselineY, n, lambda, p);

                    // 4. 写入文件
                    using (StreamWriter sw = new StreamWriter(saveFileDialog.FileName))
                    {
                        // 可以选择添加表头
                        sw.WriteLine("# X_Axis \tY_Corrected \tBaseLine");

                        for (int i = 0; i < n; i++)
                        {
                            // 使用 \t 分隔，方便导入 Origin 或 Excel
                            sw.WriteLine($"{rawX[i]}\t{correctedY[i]}\t{baselineY[i]}");
                        }
                    }

                    MessageBox.Show("数据保存成功！", "保存确认", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("帮不了一点。");
        }

        private void Baseline_Toggle(object sender, RoutedEventArgs e)
        {
            // 只有在数据加载后才执行刷新
            if (isDataLoaded)
            {
                UpdateBaselineDisplay();
            }
        }

        private void InitRangeSpan()
        {
            // 创建一个初始不可见的区间
            RangeSpan = DataCanvas.Plot.Add.HorizontalSpan(0, 0);
            RangeSpan.IsVisible = ChkEnableRange.IsChecked ?? false;
            RangeSpan.FillStyle.Color = ScottPlot.Color.FromHex("#0000FF0F"); // 半透明蓝色

            // 允许鼠标拖动边缘
            DataCanvas.UserInputProcessor.IsEnabled = true; // 确保交互开启
            RangeSpan.IsDraggable = true; // 允许拖动边界
            RangeSpan.IsResizable = true;      // 允许拉伸边缘
        }

        private void Range_Toggle(object sender, RoutedEventArgs e)
        {
            if (RangeSpan == null) return;

            bool isEnabled = ChkEnableRange.IsChecked ?? false;
            RangeSpan.IsVisible = isEnabled;

            if (isEnabled)
            {
                // 初始位置设为当前视图的中间部分
                var limits = DataCanvas.Plot.Axes.GetLimits();

                SliderStart.Minimum = limits.Left;
                SliderStart.Maximum = limits.Right;

                SliderEnd.Minimum = limits.Left;
                SliderEnd.Maximum = limits.Right;

                double width = limits.Rect.Width;
                RangeSpan.X1 = limits.Left + width * 0.25;
                RangeSpan.X2 = limits.Right - width * 0.25;

                SliderStart.Value = RangeSpan.X1;
                SliderEnd.Value = RangeSpan.X2;
            }

            DataCanvas.Refresh();
        }

        private void Parameter_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateBaselineDisplay();
        }

        private void TextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TextBox tb = sender as TextBox;
                if (tb != null)
                {
                    // 手动强制更新绑定源
                    BindingExpression be = tb.GetBindingExpression(TextBox.TextProperty);
                    be?.UpdateSource();
                }
            }
        }

        private void UpdateBaselineDisplay()
        {
            if (!isDataLoaded || rawY == null) return;
            if (pBaseline != null) DataCanvas.Plot.Remove(pBaseline);
            if (pCorrected != null) DataCanvas.Plot.Remove(pCorrected);

            // --- 逻辑：处理基线和纠正线 ---
            if (ChkRemoveBaseline.IsChecked == true)
            {
                // --- 准备计算数据 ---
                int n = rawY.Length;
                double[] correctedY = new double[n];
                double[] baselineY = new double[n];
                double lambda = Math.Pow(10, SldLambda.Value);
                double p = SldP.Value;
                // 不移除 RangeSpan 和 Markers
                

                // 调用 C++ DLL
                RemoveBaseline(rawY, correctedY, baselineY, n, lambda, p);
                lastCorrectedY = correctedY;

                // 更新基线
                pBaseline = DataCanvas.Plot.Add.Scatter(rawX, baselineY);
                pBaseline.LegendText = "Calculated Baseline";
                pBaseline.Color = ScottPlot.Colors.Red;
                pBaseline.LineStyle.Pattern = LinePattern.Dashed;
                pBaseline.LineWidth = 1;
                pBaseline.MarkerSize = 0;
                pBaseline.IsVisible = true;

                // 更新处理后曲线

                pCorrected = DataCanvas.Plot.Add.Scatter(rawX, correctedY);
                pCorrected.LegendText = "Processed Data";
                pCorrected.Color = ScottPlot.Colors.Blue;
                pCorrected.LineWidth = 2;
                pCorrected.MarkerSize = 0;
                pCorrected.IsVisible = true;
            }
            else
            {
                lastCorrectedY = rawY;
            }

            // 注意：因为没有调用 Clear()，RangeSpan 和 PeakMarkers 会一直留在图上，不需要重新 Add
            DataCanvas.Refresh();
        }

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            // 1. 基础检查
            if (!isDataLoaded || rawX == null || peakMarkers.Count == 0)
            {
                MessageBox.Show("请先加载数据并添加至少一个标记点。");
                return;
            }

            // 1. 获取用户选择的拟合类型：0 为高斯，1 为洛伦兹
            int fitType = ComboFitType.SelectedIndex;

            double[] subX;
            double[] subY;
            if (ChkEnableRange.IsChecked == true)
            {
                double xMin = Math.Min(RangeSpan.X1, RangeSpan.X2);
                double xMax = Math.Max(RangeSpan.X1, RangeSpan.X2);

                // 2. 筛选数据索引
                var indices = rawX.Select((val, idx) => new { val, idx })
                                  .Where(x => x.val >= xMin && x.val <= xMax)
                                  .Select(x => x.idx)
                                  .ToList();

                if (indices.Count < 3)
                {
                    MessageBox.Show("选取区间内数据点太少！");
                    return;
                }

                // 3. 构造截取后的数组
                subX = indices.Select(i => rawX[i]).ToArray();
                subY = indices.Select(i => lastCorrectedY[i]).ToArray();

            }
            else
            {
                // 不启用范围限制，直接使用全部数据
                subX = rawX;
                subY = lastCorrectedY;
            }

            int n = subX.Length;
            int peakCount = peakMarkers.Count;

            // 3. 准备拟合参数数组
            // centers 传入标记线的当前 X 作为初值，DLL 会将其更新为精确中心
            double[] fitCenters = peakMarkers.Select(m => m.X).ToArray();
            double[] fitAmps = new double[peakCount];
            double[] fitWidths = new double[peakCount];

            try
            {
                // 4. 调用 C++ 全局拟合
                FitPeakData(subX, subY, n, fitCenters, fitAmps, fitWidths, peakCount, fitType);

                // 5. 更新 UI 显示
                ResultList.Items.Clear();
                ResultList.Items.Add($"--- {(fitType == 0 ? "高斯" : "洛伦兹")}拟合结果 ---");

                // 开始绘图

                if (pFited != null)
                {
                    foreach (var pf in pFited)
                    {
                        DataCanvas.Plot.Remove(pf);
                    }
                    pFited.Clear();
                }
                
                int points = 150; // 拟合线的平滑度
                double[] fitX = new double[n];
                double[] fitY = new double[n];

                // 6. 遍历每一个拟合出的峰
                for (int i = 0; i < peakCount; i++)
                {
                    // 更新竖线到精确位置
                    var marker = peakMarkers[i];
                    DataCanvas.Plot.Add.Plottable(marker); // 重新把竖线加回图表
                    marker.X = fitCenters[i];
                    marker.Text = fitCenters[i].ToString("F2");
                    marker.Color = ScottPlot.Colors.Green;
                    marker.LineStyle.Pattern = LinePattern.Solid;

                    // 绘制红色的平滑拟合曲线
                    DrawSingleFitCurve(fitCenters[i], fitAmps[i], fitWidths[i], fitType);

                    // 列表显示结果
                    ResultList.Items.Add($"峰{i + 1}: 位置={fitCenters[i]:F2}, 高度={fitAmps[i]:F2}, 宽={fitWidths[i]:F2}");
                }

                DataCanvas.Refresh();
            }
            catch (Exception ex)
            {
                MessageBox.Show("拟合计算出错，请检查 DLL 是否正确加载: " + ex.Message);
            }
        }

        private void DrawSingleFitCurve(double c, double a, double w, int type)
        {
            int points = 150; // 拟合线的平滑度
            double[] fitX = new double[points];
            double[] fitY = new double[points];

            // 绘制范围设为中心点左右 4 倍宽度
            double startX = c - (w * 4);
            double endX = c + (w * 4);
            double step = (endX - startX) / (points - 1);

            for (int i = 0; i < points; i++)
            {
                double x = startX + (i * step);
                fitX[i] = x;
                if (type == 0) // Gaussian
                {
                    // y = A * exp(-(x-c)^2 / (2*w^2))
                    fitY[i] = a * Math.Exp(-Math.Pow(x - c, 2) / (2 * Math.Pow(w, 2)));
                }
                else // Lorentzian
                {
                    // y = A * (w^2 / ((x-c)^2 + w^2))
                    fitY[i] = a * (Math.Pow(w, 2) / (Math.Pow(x - c, 2) + Math.Pow(w, 2)));
                }
            }

            var pf = DataCanvas.Plot.Add.ScatterLine(fitX, fitY);
            pf.Color = ScottPlot.Colors.Red;
            pf.LineWidth = 2;
            pf.LegendText = null; // 不在图例中重复显示每一个峰
            pFited.Add(pf);
        }

        private void ClearMarkers_Click(object sender, RoutedEventArgs e)
        {
            foreach (var marker in peakMarkers)
                DataCanvas.Plot.Remove(marker);
            if (pFited != null)
            {
                foreach (var pf in pFited)
                {
                    DataCanvas.Plot.Remove(pf);
                }
                pFited.Clear();
            }
            peakMarkers.Clear();
            ResultList.Items.Clear();
            UpdateBaselineDisplay();
        }

        private void ResetView_Click(object sender, RoutedEventArgs e)
        {
            // 重置主视图
            UpdateBaselineDisplay();
            DataCanvas.Plot.Axes.AutoScale();
            DataCanvas.Refresh();

            foreach (var marker in peakMarkers)
                DataCanvas.Plot.Remove(marker);
        }

        private void AddMarker_Click(object sender, RoutedEventArgs e)
        {
            if (!isDataLoaded) return;

            var limits = DataCanvas.Plot.Axes.GetLimits();
            double centerX = (limits.Left + limits.Right) / 2;

            var vl = DataCanvas.Plot.Add.VerticalLine(centerX);
            vl.Color = ScottPlot.Colors.Orange;
            vl.LineWidth = 2;
            vl.LineStyle.Pattern = LinePattern.Dashed;

            // --- 关键设置 ---
            vl.Text = centerX.ToString("F2"); // 设置初始文字内容
            vl.LabelFontSize = 20;            // 设置字体大小
            vl.LabelOppositeAxis = false;      // 如果标签在上方挡住了图表，可以设为 true 移到下方
                                              // ----------------

            activeMarker = vl;
            peakMarkers.Add(vl);

            MarkerSlider.IsEnabled = true;
            MarkerSlider.Minimum = limits.Left;
            MarkerSlider.Maximum = limits.Right;
            MarkerSlider.Value = centerX;

            DataCanvas.Refresh();
        }

        private void MarkerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (activeMarker == null) return;

            // 1. 改变线的位置
            activeMarker.X = e.NewValue;

            // 2. 实时更新线条上的标签内容
            activeMarker.Text = e.NewValue.ToString("F2");

            // 4. 重绘
            DataCanvas.Refresh();
        }

        private void RangeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // 确保数据已加载且 RangeSpan 已初始化
            if (!isDataLoaded || RangeSpan == null) return;

            // 更新 ScottPlot 选区
            RangeSpan.X1 = Math.Min(SliderStart.Value, SliderEnd.Value);
            RangeSpan.X2 = Math.Max(SliderStart.Value, SliderEnd.Value);

            DataCanvas.Refresh();
        }

        private void CopyResults_Click(object sender, RoutedEventArgs e)
        {
            // 1. 检查列表是否为空
            if (ResultList.Items.Count == 0)
            {
                MessageBox.Show("没有可复制的结果！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. 使用 StringBuilder 拼接所有行
            StringBuilder sb = new StringBuilder();
            foreach (var item in ResultList.Items)
            {
                // 假设 ResultList 里的项是字符串，或者是重写了 ToString() 的对象
                sb.AppendLine(item.ToString());
            }

            try
            {
                // 3. 设置到剪切板
                Clipboard.SetText(sb.ToString());

                // 4. 给用户一个简单的反馈（可选，也可以不弹窗，直接改按钮文字）
                MessageBox.Show("结果已成功复制到剪切板！", "成功", MessageBoxButton.OK, MessageBoxImage.Asterisk);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

    }
}