using System.Collections.Concurrent;

namespace RoboMaster;

public class Feed<T> : IObservable<T>
{
    public event Action? OnHasObservers;
    public event Action? OnNoObservers;

    private readonly ConcurrentDictionary<IObserver<T>, Unsubscriber> observers = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        if (observers.IsEmpty) OnHasObservers?.Invoke();

        var unsubscriber = new Unsubscriber(observers, observer, OnNoObservers);
        observers.TryAdd(observer, unsubscriber);
        return unsubscriber;
    }

    private class Unsubscriber : IDisposable
    {
        private Action? _onNoObservers;
        private readonly ConcurrentDictionary<IObserver<T>, Unsubscriber> _observers;
        private readonly IObserver<T> _observer;

        public Unsubscriber(ConcurrentDictionary<IObserver<T>, Unsubscriber> observers, IObserver<T> observer, Action? onNoObservers)
        {
            _observers = observers;
            _observer = observer;
            _onNoObservers = onNoObservers;
        }

        public void Dispose()
        {
            if (_observer != null && _observers.ContainsKey(_observer))
                _observers.TryRemove(new KeyValuePair<IObserver<T>, Unsubscriber>(_observer, this));

            if (_observers.IsEmpty) _onNoObservers?.Invoke();
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
