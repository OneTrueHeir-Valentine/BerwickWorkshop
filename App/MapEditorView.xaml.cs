using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Berwick.Workshop;

public partial class MapEditorView : UserControl
{
    public MapEditorView() => InitializeComponent();

    internal event SelectionChangedEventHandler? BattleSelected;
    internal event RoutedEventHandler? OptionsChanged;
    internal event MouseButtonEventHandler? CanvasLeftClicked;
    internal event MouseButtonEventHandler? CanvasRightClicked;
    internal event RoutedEventHandler? EditUnitRequested;

    private void OnBattleSelected(object sender, SelectionChangedEventArgs e) => BattleSelected?.Invoke(this, e);
    private void OnOptionsChanged(object sender, RoutedEventArgs e) => OptionsChanged?.Invoke(this, e);
    private void OnCanvasLeftClicked(object sender, MouseButtonEventArgs e) => CanvasLeftClicked?.Invoke(this, e);
    private void OnCanvasRightClicked(object sender, MouseButtonEventArgs e) => CanvasRightClicked?.Invoke(this, e);
    private void OnEditUnitRequested(object sender, RoutedEventArgs e) => EditUnitRequested?.Invoke(this, e);

    internal ListBox MapListControl => MapList;
    internal CheckBox MapArtBoxControl => MapArtBox;
    internal CheckBox MapTerrainBoxControl => MapTerrainBox;
    internal CheckBox MapUnitsBoxControl => MapUnitsBox;
    internal CheckBox MapArrivalsBoxControl => MapArrivalsBox;
    internal CheckBox MapMoveBoxControl => MapMoveBox;
    internal CheckBox MapDeployBoxControl => MapDeployBox;
    internal ComboBox MapSizeBoxControl => MapSizeBox;
    internal Canvas MapCanvasControl => MapCanvas;
    internal Border MapUnitBoxControl => MapUnitBox;
    internal Button EditMapUnitButtonControl => EditMapUnitButton;
    internal TextBlock MapUnitNameControl => MapUnitName;
    internal StackPanel MapUnitFieldsControl => MapUnitFields;
    internal StackPanel MapUnitEditorPanelControl => MapUnitEditorPanel;
    internal StackPanel MapDetailsControl => MapDetails;
    internal TextBlock MapCountControl => MapCount;
}
