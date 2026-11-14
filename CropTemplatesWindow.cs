using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace QuickCrop;

public sealed class CropTemplatesWindow : Window
{
    private sealed record KindOption(CropTemplateKind Kind, string Title);
    private readonly ObservableCollection<CropTemplate> templates;
    private readonly DataGrid table;
    private readonly ComboBox[] pins = new ComboBox[5];
    private readonly string[] previousPins = new string[5];
    private readonly TextBlock error;
    private bool syncing;
    public List<CropTemplate> Templates => templates.Select(t => t.Clone()).ToList();
    public List<string> PinnedIds => pins.Select(p => (string)p.SelectedValue).ToList();

    public CropTemplatesWindow(AppSettings settings)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "尺寸模板 · 轻裁"; Width = 800; Height = 620; MinWidth = 660; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        templates = new ObservableCollection<CropTemplate>(settings.CropTemplates.Select(t => t.Clone()));
        var root = new DockPanel { Margin = new Thickness(22) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = IconButton("\uE710", "新建模板"); add.Click += (_, _) => AddTemplate();
        var delete = IconButton("\uE74D", "删除所选模板"); delete.Click += (_, _) => DeleteTemplate();
        commands.Children.Add(add); commands.Children.Add(delete); DockPanel.SetDock(commands, Dock.Right); header.Children.Add(commands);
        header.Children.Add(new TextBlock { Text = "尺寸模板", FontSize = 18, FontWeight = FontWeights.SemiBold });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "前台五个尺寸", Margin = new Thickness(0, 0, 0, 8) });
        var pinGrid = new UniformGrid { Columns = 5, Rows = 1 };
        syncing = true;
        for (int i = 0; i < pins.Length; i++)
        {
            var pin = new ComboBox { ItemsSource = templates, DisplayMemberPath = nameof(CropTemplate.DisplayLabel), SelectedValuePath = nameof(CropTemplate.Id), MinWidth = 0, Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), ToolTip = $"前台位置 {i + 1}" };
            int slot = i;
            pin.SelectedValue = previousPins[i] = settings.PinnedCropTemplateIds[i];
            pin.SelectionChanged += (_, _) => ChangePin(slot);
            pins[i] = pin; pinGrid.Children.Add(pin);
        }
        syncing = false;
        footer.Children.Add(pinGrid);
        error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) }; footer.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary") };
        save.Click += (_, _) => SaveTemplates();
        buttons.Children.Add(cancel); buttons.Children.Add(save); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(47, 52, 52))));
        headerStyle.Setters.Add(new Setter(ForegroundProperty, Brushes.WhiteSmoke));
        headerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 7, 8, 7)));
        table = new DataGrid
        {
            ItemsSource = templates, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, SelectionMode = DataGridSelectionMode.Single,
            Background = Brushes.Transparent, Foreground = Brushes.WhiteSmoke, RowBackground = new SolidColorBrush(Color.FromRgb(32, 34, 34)),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(40, 43, 43)), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = (Brush)FindResource("Line"), BorderBrush = (Brush)FindResource("Line"), ColumnHeaderStyle = headerStyle,
            RowHeight = 38, MinColumnWidth = 64
        };
        table.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = Editable(nameof(CropTemplate.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "类型", ItemsSource = new[] { new KindOption(CropTemplateKind.Ratio, "比例"), new KindOption(CropTemplateKind.Pixels, "像素") },
            DisplayMemberPath = "Title", SelectedValuePath = "Kind", SelectedValueBinding = Editable(nameof(CropTemplate.Kind)), Width = 88
        });
        table.Columns.Add(new DataGridTextColumn { Header = "宽", Binding = Editable(nameof(CropTemplate.Width)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        table.Columns.Add(new DataGridTextColumn { Header = "高", Binding = Editable(nameof(CropTemplate.Height)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        root.Children.Add(table); Content = root;
    }
    private static Binding Editable(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true };
    private Button IconButton(string symbol, string tooltip) => new() { Content = symbol, ToolTip = tooltip, Style = (Style)FindResource("IconButton"), Margin = new Thickness(6, 0, 0, 0) };
    private void AddTemplate()
    {
        int n = 1;
        while (templates.Any(t => t.Name == $"模板 {n}")) n++;
        var template = new CropTemplate { Name = $"模板 {n}", Width = 1, Height = 1 };
        templates.Add(template); table.SelectedItem = template; table.ScrollIntoView(template);
    }
    private void DeleteTemplate()
    {
        if (table.SelectedItem is not CropTemplate template) return;
        if (templates.Count <= 5) { error.Text = "至少保留五个尺寸模板。"; return; }
        syncing = true;
        var selected = pins.Select(p => p.SelectedValue as string).ToArray();
        templates.Remove(template);
        for (int i = 0; i < pins.Length; i++)
        {
            string id = selected[i] == template.Id ? templates.First(t => !selected.Contains(t.Id)).Id : selected[i]!;
            pins[i].SelectedValue = previousPins[i] = id;
        }
        syncing = false; error.Text = "";
    }
    private void ChangePin(int slot)
    {
        if (syncing || pins[slot].SelectedValue is not string id) return;
        syncing = true;
        for (int i = 0; i < pins.Length; i++)
            if (i != slot && (string?)pins[i].SelectedValue == id) pins[i].SelectedValue = previousPins[slot];
        for (int i = 0; i < pins.Length; i++) previousPins[i] = (string)pins[i].SelectedValue;
        syncing = false;
    }
    private void SaveTemplates()
    {
        try
        {
            if (!table.CommitEdit(DataGridEditingUnit.Cell, true) || !table.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidOperationException("请检查模板数值。");
            foreach (var template in templates) { template.Name = template.Name.Trim(); template.Validate(); }
            if (templates.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != templates.Count) throw new InvalidOperationException("模板名称不能重复。");
            if (PinnedIds.Distinct().Count() != 5) throw new InvalidOperationException("前台需要五个不同的尺寸模板。");
            DialogResult = true;
        }
        catch (Exception ex) { error.Text = ex.Message; }
    }
}
