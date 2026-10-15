// Одновременно подключены WPF и Windows Forms (из Forms берётся только
// системный диалог выбора цвета), поэтому часть имён совпадает. Псевдонимы
// фиксируют, что берётся из WPF.
global using Color = System.Windows.Media.Color;
global using Colors = System.Windows.Media.Colors;
global using Point = System.Windows.Point;
global using Brush = System.Windows.Media.Brush;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using RenderTargetBitmap = System.Windows.Media.Imaging.RenderTargetBitmap;
