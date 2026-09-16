using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Evania.Services;

namespace Evania.ViewModels
{
    /// <summary>
    /// Backs the Chat page. The page is nothing but a browser pointed at whichever provider the
    /// settings say, so this view model only resolves that choice into a URL; switching providers
    /// in Settings lands here through the settings object the page subscribes to.
    /// </summary>
    public partial class ChatPageViewModel : ObservableObject
    {
        /// <summary>The live settings instance the page reads the provider from.</summary>
        public AppSettings Settings { get; } = SettingsService.Current;

        /// <summary>The address of the currently chosen provider, falling back to Copilot when the
        /// value typed in Settings is not a usable absolute URL.</summary>
        public Uri ChatUrl
        {
            get
            {
                if (Uri.TryCreate(Settings.ChatProviderUrl, UriKind.Absolute, out Uri? url)
                    && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
                {
                    return url;
                }

                return new Uri(ChatProviderDefaults.Url);
            }
        }
    }
}
