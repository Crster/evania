using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Evania.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Evania.ViewModels
{
    public partial class NavigationItemViewModel : ObservableObject
    {
        [ObservableProperty]
        public partial string Content { get; set; }

        [ObservableProperty]
        public partial string Tag { get; set; }

        [ObservableProperty]
        public partial IconElement Icon { get; set; }
    }

    public partial class MainViewModel : ObservableObject
    {
        public const string BookmarkTagPrefix = "Bookmark:";

        public ObservableCollection<NavigationItemViewModel> MenuItems { get; }

        // Selection belongs to the NavigationView. Mirroring it here would mean holding an item the
        // view model never made - the settings entry is the control's own - and the binding that
        // kept the two in step would have to cast it to a type it is not.

        // Bookmarks now load with the database, on the background thread that opens it, so the
        // change can arrive off the UI thread even though MenuItems is bound to the NavigationView.
        private readonly DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        public MainViewModel()
        {
            MenuItems = [];
            RebuildMenuItems();

            SettingsService.Current.PropertyChanged += Settings_PropertyChanged;
        }

        private static IReadOnlyList<NavigationItemViewModel> StaticMenuItems =>
        [
            new NavigationItemViewModel
            {
                Content = "Chat",
                Tag = nameof(Views.ChatPage),
                Icon = CreateGlyphIcon("\uE8BD")
            },
            new NavigationItemViewModel
            {
                Content = "Notes",
                Tag = nameof(Views.NotePage),
                Icon = CreateGlyphIcon("\uE70B")
            },
            new NavigationItemViewModel
            {
                Content = "Screenshot",
                Tag = nameof(Views.ScreenshotPage),
                Icon = CreateGlyphIcon("\uF406")
            },
            new NavigationItemViewModel
            {
                Content = "Screen Record",
                Tag = nameof(Views.ScreenRecordPage),
                Icon = CreateGlyphIcon("\uF7EE")
            },
            new NavigationItemViewModel
            {
                Content = "Vault",
                Tag = nameof(Views.VaultPage),
                Icon = CreateGlyphIcon("\uE72E")
            }
        ];

        private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(AppSettings.Bookmarks))
            {
                return;
            }

            if (dispatcherQueue is null || dispatcherQueue.HasThreadAccess)
            {
                RebuildMenuItems();

                return;
            }

            dispatcherQueue.TryEnqueue(RebuildMenuItems);
        }

        private void RebuildMenuItems()
        {
            MenuItems.Clear();

            foreach (NavigationItemViewModel item in StaticMenuItems)
            {
                MenuItems.Add(item);
            }

            foreach (WebBookmark bookmark in SettingsService.Current.Bookmarks)
            {
                if (!Uri.TryCreate(bookmark.Url, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    continue;
                }

                string title = string.IsNullOrWhiteSpace(bookmark.Title)
                    ? uri.Host
                    : bookmark.Title.Trim();

                MenuItems.Add(new NavigationItemViewModel
                {
                    Content = title,
                    Tag = BookmarkTagPrefix + uri.AbsoluteUri,
                    Icon = CreateFaviconIcon(uri)
                });
            }
        }

        private static FontIcon CreateGlyphIcon(string glyph) => new()
        {
            Glyph = glyph,
        };

        private static BitmapIcon CreateFaviconIcon(Uri siteUri)
        {
            UriBuilder builder = new(siteUri)
            {
                Path = "/favicon.ico",
                Query = string.Empty,
                Fragment = string.Empty,
            };

            return new BitmapIcon
            {
                UriSource = builder.Uri,
                ShowAsMonochrome = false,
            };
        }
    }
}
