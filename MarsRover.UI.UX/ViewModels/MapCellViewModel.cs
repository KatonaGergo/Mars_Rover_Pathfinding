using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MarsRover.ViewModels;

public partial class MapCellViewModel : ViewModelBase
{
    [ObservableProperty]
    private IBrush _color = Brushes.DimGray;
}