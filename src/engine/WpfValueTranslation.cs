using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace CMOLocalizer
{
    public partial class CustomAppDomainManager
    {
        private static readonly ConditionalWeakTable<FrameworkElement, WpfValueTranslationState> wpfValueTranslations =
            new ConditionalWeakTable<FrameworkElement, WpfValueTranslationState>();
        private static readonly object wpfValueSubscriptionLock = new object();

        private static void AttachWpfValueTranslation(FrameworkElement target)
        {
            // The table uses object identity and does not root an unloaded element.
            // The element and all callbacks remain on its owning Dispatcher.
            WpfValueTranslationState state = wpfValueTranslations.GetValue(target,
                delegate(FrameworkElement owner) { return new WpfValueTranslationState(owner); });
            state.AttachIfLoaded();
        }

        private sealed class WpfValueTranslationState
        {
            private readonly FrameworkElement owner;
            private readonly DependencyPropertyDescriptor descriptor;
            private bool attached;

            internal WpfValueTranslationState(FrameworkElement owner)
            {
                this.owner = owner;
                descriptor = owner is TextBlock
                    ? DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                    : DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
                owner.Loaded += OnLoaded;
                owner.Unloaded += OnDetached;
            }

            internal void AttachIfLoaded()
            {
                // DPD itself retains its component strongly. A weak callback alone
                // would still leak controls translated before they ever load.
                if (attached || !owner.IsLoaded || owner.Dispatcher.HasShutdownStarted) return;
                // WPF caches the descriptor across Dispatchers, but its tracker
                // dictionary does not synchronize Add/RemoveValueChanged.
                lock (wpfValueSubscriptionLock) descriptor.AddValueChanged(owner, OnValueChanged);
                attached = true;
                owner.Dispatcher.ShutdownStarted += OnDetached;
            }

            private void OnLoaded(object sender, RoutedEventArgs args)
            {
                AttachIfLoaded();
                TranslateWpfElement(owner);
            }

            private void OnDetached(object sender, EventArgs args)
            {
                // Dispatcher shutdown does not guarantee an Unloaded event.
                Detach();
            }

            private void Detach()
            {
                if (!attached) return;
                owner.Dispatcher.ShutdownStarted -= OnDetached;
                lock (wpfValueSubscriptionLock) descriptor.RemoveValueChanged(owner, OnValueChanged);
                attached = false;
            }

            private void OnValueChanged(object sender, EventArgs args)
            {
                if (!owner.IsLoaded || IsEditableWpfTarget(owner) || _inWpfTextChanged) return;
                if (!EnterMutation("WpfValueHook")) return;
                try
                {
                    _inWpfTextChanged = true;
                    TextBlock text = owner as TextBlock;
                    if (text != null)
                    {
                        string current = text.Text;
                        if (string.IsNullOrEmpty(current)) return;
                        string mixed = FixMixedEnglishPrefix(current);
                        if (mixed != null) { SetTextKeepBinding(text, mixed); return; }
                        if (current.Contains("kg") && (current.Contains("fuel") || current.Contains("燃油")
                            || current.Contains("Tank") || current.Contains("坦克") || current.Contains("reserve")))
                        {
                            string fuel = TranslateFuelPanelText(current);
                            if (fuel != current) { SetTextKeepBinding(text, fuel); return; }
                        }
                        if (!HasAsciiLetter(current)) return;
                        string translated = TranslateString(current);
                        if (string.IsNullOrEmpty(translated) || translated == current)
                            translated = TranslateMilitaryUnitName(current);
                        if (translated != current) SetTextKeepBinding(text, translated);
                    }
                    else
                    {
                        ContentControl content = (ContentControl)owner;
                        string current = content.Content as string;
                        if (string.IsNullOrEmpty(current)) return;
                        string mixed = FixMixedEnglishPrefix(current);
                        if (mixed != null) { SetContentKeepBinding(content, mixed); return; }
                        if (!HasAsciiLetter(current)) return;
                        string translated = TranslateString(current);
                        if (translated != current) SetContentKeepBinding(content, translated);
                    }
                }
                catch { }
                finally { _inWpfTextChanged = false; ExitMutation(); }
            }
        }
    }
}
