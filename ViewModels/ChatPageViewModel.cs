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

        /// <summary>The address of the currently chosen provider.</summary>
        public Uri ChatUrl => new(Settings.ChatProvider.ToUrl());
    }
}
