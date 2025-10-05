// Global type aliases to prefer WPF types when Windows Forms is enabled
global using Application = System.Windows.Application;
global using UserControl = System.Windows.Controls.UserControl;
global using Brushes = System.Windows.Media.Brushes;
global using Brush = System.Windows.Media.Brush;
global using Color = System.Windows.Media.Color;
global using SolidColorBrush = System.Windows.Media.SolidColorBrush;
global using ColorConverter = System.Windows.Media.ColorConverter;
global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
// Additional WPF-first aliases to avoid ambiguities with WinForms
global using MessageBox = System.Windows.MessageBox;
global using ListBox = System.Windows.Controls.ListBox;
global using TreeView = System.Windows.Controls.TreeView;
global using RadioButton = System.Windows.Controls.RadioButton;
global using CheckBox = System.Windows.Controls.CheckBox;
global using Clipboard = System.Windows.Clipboard;
