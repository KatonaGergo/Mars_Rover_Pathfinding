using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MarsRover.ViewModels;

// Figyelj rá, hogy ne legyen másik "class MapCellViewModel" körötte!
// Az osztálynak "partial"-nak kell lennie és a ViewModelBase-ből kell származnia.
public partial class MapCellViewModel : ViewModelBase
{
    [ObservableProperty]
    private IBrush _color = Brushes.DimGray;
}