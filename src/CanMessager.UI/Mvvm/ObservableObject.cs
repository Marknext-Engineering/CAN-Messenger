using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CanMessager.UI.Mvvm;

/// <summary>INotifyPropertyChanged 경량 베이스.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>field에 value를 대입하고 변경 시 PropertyChanged 발생. 추가로 갱신할 속성명을 also로 전달 가능.</summary>
    protected bool SetProperty<T>(ref T field, T value,
        [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        foreach (var n in also) OnPropertyChanged(n);
        return true;
    }
}
