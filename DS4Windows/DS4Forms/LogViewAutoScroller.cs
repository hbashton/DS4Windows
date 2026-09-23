using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DS4WinWPF.DS4Forms
{
    /// <summary>One deferred scroll for a visible batch, never one per log message.</summary>
    internal sealed class LogViewAutoScroller : IDisposable
    {
        private readonly ListView view;
        private readonly Func<bool> isVisible;
        private readonly Action<object> scrollIntoView;
        private DispatcherOperation pendingScroll;
        private bool disposed;

        internal LogViewAutoScroller(ListView view, Func<bool> isVisible = null,
            Action<object> scrollIntoView = null)
        {
            this.view = view ?? throw new ArgumentNullException(nameof(view));
            view.Dispatcher.VerifyAccess();
            this.isVisible = isVisible ?? (() => view.IsVisible);
            this.scrollIntoView = scrollIntoView ?? view.ScrollIntoView;
            // Observe WPF's dispatcher-owned view, not the producer collection.
            ((INotifyCollectionChanged)view.Items).CollectionChanged += ItemsChanged;
            view.IsVisibleChanged += VisibilityChanged;
        }

        private void ItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add ||
                e.Action == NotifyCollectionChangedAction.Reset)
                RequestScroll();
        }

        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            RequestScroll();
        }

        internal void RequestScroll()
        {
            view.Dispatcher.VerifyAccess();
            if (disposed) return;
            if (!isVisible())
            {
                pendingScroll?.Abort();
                pendingScroll = null;
                return;
            }
            if (view.Items.Count == 0 || view.Dispatcher.HasShutdownStarted ||
                pendingScroll?.Status == DispatcherOperationStatus.Pending)
                return;

            pendingScroll = view.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(ScrollToLatest));
        }

        private void ScrollToLatest()
        {
            pendingScroll = null;
            if (disposed || !isVisible() || view.Items.Count == 0) return;
            // Collection views may lag behind their source. Use the same view
            // for count and item, and resolve the tail only when this runs.
            scrollIntoView(view.Items[view.Items.Count - 1]);
        }

        public void Dispose()
        {
            view.Dispatcher.VerifyAccess();
            if (disposed) return;
            disposed = true;
            ((INotifyCollectionChanged)view.Items).CollectionChanged -= ItemsChanged;
            view.IsVisibleChanged -= VisibilityChanged;
            pendingScroll?.Abort();
            pendingScroll = null;
        }
    }
}
