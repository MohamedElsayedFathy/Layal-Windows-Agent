using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Layal.Models;

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private string _displayRole = string.Empty;
    private FlowDirection _direction = FlowDirection.LeftToRight;

    public string Role { get; init; } = "assistant";

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            OnPropertyChanged();
        }
    }

    public string DisplayRole
    {
        get => _displayRole;
        set
        {
            if (_displayRole == value) return;
            _displayRole = value;
            OnPropertyChanged();
        }
    }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public FlowDirection Direction
    {
        get => _direction;
        set
        {
            if (_direction == value) return;
            _direction = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
