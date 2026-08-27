using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Evania.ViewModels
{
    public partial class NavigationItemViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial string Content { get; set; }

        [ObservableProperty]
        public partial string Tag { get; set; }

        [ObservableProperty]
        public partial string IconGlyph { get; set; }

        // Selection belongs to the NavigationView; mirroring it here would let the two bindings
        // fight over which item is selected.
    }

    public partial class MainViewModel : ObservableObject
    {
        public ObservableCollection<NavigationItemViewModel> MenuItems { get; }

        [ObservableProperty]
        public partial NavigationItemViewModel? SelectedMenuItem { get; set; }

        public MainViewModel()
        {
            MenuItems = new ObservableCollection<NavigationItemViewModel>
            {
                new NavigationItemViewModel
                {
                    Content = "Screenshot",
                    Tag = "ScreenshotPage",
                    IconGlyph = "\uF406"
                },
                new NavigationItemViewModel
                {
                    Content = "Screen Record",
                    Tag = "ScreenRecordPage",
                    IconGlyph = "\uF7EE"
                }
            };
            SelectedMenuItem = MenuItems[0];
        }
    }
}