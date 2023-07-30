using System.Collections.Concurrent;

namespace RoboMaster;

public class Feed<T> : IObservable<T>
{
    private readonly ConcurrentDictionary<IObserver<T>, Unsubscriber> observers = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        var unsubscriber = new Unsubscriber(this, observer);
        observers.TryAdd(observer, unsubscriber);
        return unsubscriber;
    }

    private class Unsubscriber : IDisposable
    {
        private readonly WeakReference<Feed<T>> _feed;
        private readonly IObserver<T> _observer;

        public Unsubscriber(Feed<T> feed, IObserver<T> observer)
        {
            _feed = new WeakReference<Feed<T>>(feed);
            _observer = observer;
        }

        public void Dispose()
        {
            if (_feed.TryGetTarget(out var feed))
            {
                if (_observer != null && feed.observers.ContainsKey(_observer))
                    feed.observers.TryRemove(new KeyValuePair<IObserver<T>, Unsubscriber>(_observer, this));
            }
        }
    }

    public void Notify(T data)
    {
        foreach (var observer in observers.Keys)
        {
            observer.OnNext(data);
        }
    }
}
