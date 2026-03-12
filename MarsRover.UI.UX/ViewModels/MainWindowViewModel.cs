using System.Collections.ObjectModel;
using Avalonia.Media;

namespace MarsRover.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<MapCellViewModel> MapCells { get; } = new();
        public MainWindowViewModel()
        {
            for (int i = 0; i < 2500; i++)
            {
                var cell = new MapCellViewModel { Color = Brushes.DimGray };

                MapCells.Add(cell);
            }
        }
    }
}