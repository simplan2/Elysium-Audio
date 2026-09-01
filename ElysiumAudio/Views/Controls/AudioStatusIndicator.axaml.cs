using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ElysiumAudio.Models;
using System;

namespace ElysiumAudio.Views.Controls;

public partial class AudioStatusIndicator : UserControl
{
    public static readonly StyledProperty<AudioFileStatus> StatusProperty =
    AvaloniaProperty.Register<AudioStatusIndicator, AudioFileStatus>(
        nameof(Status),
        AudioFileStatus.Pending);

    public AudioFileStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private readonly Grid _spinnerHost;
    private readonly RotateTransform _rotateTransform;
    private readonly DispatcherTimer _timer;

    public AudioStatusIndicator()
    {
        InitializeComponent();

        _spinnerHost = this.FindControl<Grid>("SpinnerHost")!;
        _rotateTransform = (RotateTransform)_spinnerHost.RenderTransform!;

        // Centro de rotación en píxeles: punto medio exacto del Grid 32x32
        _rotateTransform.CenterX = 16;
        _rotateTransform.CenterY = 16;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;

        UpdateStatus(Status);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _rotateTransform.Angle = (_rotateTransform.Angle + 6) % 360;
    }

    protected override void OnPropertyChanged(
        AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StatusProperty)
        {
            UpdateStatus(change.GetNewValue<AudioFileStatus>());
        }
    }

    private void UpdateStatus(AudioFileStatus status)
    {
        bool isActive = status is AudioFileStatus.Analyzing
            or AudioFileStatus.Processing
            or AudioFileStatus.Tagging;

        SpinnerHost.IsVisible = isActive;
        CheckIcon.IsVisible = status == AudioFileStatus.Completed;
        ErrorIcon.IsVisible = status == AudioFileStatus.Error;
        PendingIcon.IsVisible = status == AudioFileStatus.Pending;

        if (isActive)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _rotateTransform.Angle = 0;
        }
    }

    protected override void OnDetachedFromVisualTree(
        VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}