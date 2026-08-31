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
    }

    public partial class MainViewModel : ObservableObject
    {
        public ObservableCollection<NavigationItemViewModel> MenuItems { get; }

        // Selection belongs to the NavigationView. Mirroring it here would mean holding an item the
        // view model never made - the settings entry is the control's own - and the binding that
        // kept the two in step would have to cast it to a type it is not.

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
        }
    }
}