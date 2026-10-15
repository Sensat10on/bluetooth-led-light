global using System.IO;
global using OpenTuyaSync.Providers;

// Одновременно подключены WPF и Windows Forms (из Forms берётся только
// значок в трее и системный диалог выбора цвета), поэтому часть имён
// совпадает. Псевдонимы фиксируют, что берётся из WPF.
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using Button = System.Windows.Controls.Button;
global using TextBox = System.Windows.Controls.TextBox;
global using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
global using ListBoxItem = System.Windows.Controls.ListBoxItem;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
