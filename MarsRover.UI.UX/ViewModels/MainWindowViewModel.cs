using System.Collections.ObjectModel;
using Avalonia.Media;

namespace MarsRover.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public ObservableCollection<MapCellViewModel> MapCells { get; } = new();

        // --- EZT A RÉSZT ADDD HOZZÁ ---
        public MainWindowViewModel()
        {
            // Legyártunk 2500 cellát (50x50-es rács)
            for (int i = 0; i < 2500; i++)
            {
                // Alapértelmezett szürke szín minden cellának
                var cell = new MapCellViewModel { Color = Brushes.DimGray };

                MapCells.Add(cell);
            }
        }
    }
}