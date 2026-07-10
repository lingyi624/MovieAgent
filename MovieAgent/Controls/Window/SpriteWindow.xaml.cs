using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;

namespace MovieAgent.Controls.Window;

public enum SpriteState
{
    Sleeping,
    Idle,
    Listening,
    Speaking,
    Working,
    Thinking,
    Surprised
}

public partial class SpriteWindow : System.Windows.Window
{
    private SpriteState _currentState = SpriteState.Listening;
    private bool _isDragging;
    private Point _dragStartPoint;
    private Point _windowStartPoint;
    

    public SpriteState CurrentState
    {
        get => _currentState;
        set
        {
            _currentState = value;
            UpdateAnimation();
        }
    }

    public string SpeechText
    {
        get => SpeechTextBlock.Text;
        set
        {
            SpeechTextBlock.Text = value;
            SpeechBubble.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public bool IsTyping
    {
        get => TypingIndicator.Visibility == Visibility.Visible;
        set => TypingIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public event EventHandler? SpriteClicked;
    public new event EventHandler<SpriteState>? StateChanged;

    public SpriteWindow()
    {
        InitializeComponent();
        LoadPosition();
        CurrentState = SpriteState.Sleeping;
        

        
    }
    
    
    
     
    
    

    private void LoadPosition()
    {
        try
        {
            var saved = System.Windows.Application.Current.Properties["SpritePosition"] as string;
            if (!string.IsNullOrEmpty(saved))
            {
                var parts = saved.Split(',');
                if (parts.Length == 2 && 
                    double.TryParse(parts[0], out double x) && 
                    double.TryParse(parts[1], out double y))
                {
                    Left = x;
                    Top = y;
                }
            }
            else
            {
                Left = System.Windows.SystemParameters.WorkArea.Width - Width - 20;
                Top = System.Windows.SystemParameters.WorkArea.Height - Height - 50;
            }
        }
        catch
        {
            Left = System.Windows.SystemParameters.WorkArea.Width - Width - 20;
            Top = System.Windows.SystemParameters.WorkArea.Height - Height - 50;
        }
    }

    private void SavePosition()
    {
        try
        {
            System.Windows.Application.Current.Properties["SpritePosition"] = $"{Left},{Top}";
        }
        catch { }
    }

    private void UpdateAnimation()
    {
        StopAllAnimations();
        
        switch (_currentState)
        {
            case SpriteState.Sleeping:
                ShowSleepElements(true);
                ShowWorkElements(false);
                ShowListeningElements(false);
                Head.RenderTransform = new RotateTransform(-15);
                LeftEye.Height = 2;
                RightEye.Height = 2;
                Mouth.StrokeThickness = 1;
                LeftArm.RenderTransform = new RotateTransform(-45);
                RightArm.RenderTransform = new RotateTransform(45);
                BeginAnimation("SleepingAnimation");
                break;
                
            case SpriteState.Idle:
                ShowSleepElements(false);
                ShowWorkElements(false);
                ShowListeningElements(false);
                Head.RenderTransform = new RotateTransform(0);
                LeftEye.Height = 8;
                RightEye.Height = 8;
                Mouth.StrokeThickness = 1.5;
                LeftArm.RenderTransform = new RotateTransform(0);
                RightArm.RenderTransform = new RotateTransform(0);
                BeginAnimation("IdleAnimation");
                break;
                
            case SpriteState.Listening:
                ShowSleepElements(false);
                ShowWorkElements(false);
                ShowListeningElements(true);
                Head.RenderTransform = new RotateTransform(0);
                LeftEye.Height = 8;
                RightEye.Height = 8;
                Mouth.StrokeThickness = 1.5;
                LeftArm.RenderTransform = new RotateTransform(-15);
                RightArm.RenderTransform = new RotateTransform(15);
                BeginAnimation("ListeningAnimation");
                break;
                
            case SpriteState.Speaking:
                ShowSleepElements(false);
                ShowWorkElements(false);
                ShowListeningElements(false);
                Head.RenderTransform = new RotateTransform(0);
                LeftEye.Height = 8;
                RightEye.Height = 8;
                Mouth.StrokeThickness = 1.5;
                LeftArm.RenderTransform = new RotateTransform(0);
                RightArm.RenderTransform = new RotateTransform(0);
                BeginAnimation("SpeakingAnimation");
                break;
                
            case SpriteState.Working:
                ShowSleepElements(false);
                ShowWorkElements(true);
                ShowListeningElements(false);
                Head.RenderTransform = new RotateTransform(0);
                LeftEye.Height = 8;
                RightEye.Height = 8;
                Mouth.StrokeThickness = 1.5;
                LeftArm.RenderTransform = new RotateTransform(0);
                RightArm.RenderTransform = new RotateTransform(0);
                BeginAnimation("WorkingAnimation");
                break;
                
            case SpriteState.Thinking:
                ShowSleepElements(false);
                ShowWorkElements(false);
                ShowListeningElements(false);
                Head.RenderTransform = new RotateTransform(0);
                LeftEye.Height = 8;
                RightEye.Height = 8;
                Mouth.StrokeThickness = 1.5;
                LeftArm.RenderTransform = new RotateTransform(0);
                RightArm.RenderTransform = new RotateTransform(0);
                BeginAnimation("ThinkingAnimation");
                break;
                
            case SpriteState.Surprised:
                ShowSleepElements(false);
                ShowWorkElements(false);
                ShowListeningElements(false);
                Head.RenderTransform = new ScaleTransform(1.15, 1.15);
                LeftEye.Height = 8;
                RightEye.Height = 8;
                Mouth.StrokeThickness = 2;
                LeftArm.RenderTransform = new RotateTransform(0);
                RightArm.RenderTransform = new RotateTransform(0);
                BeginAnimation("SurprisedAnimation");
                break;
        }
        
        StateChanged?.Invoke(this, _currentState);
    }

    private void ShowSleepElements(bool show)
    {
        ZText1.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ZText2.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ZText3.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SleepIcon.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowWorkElements(bool show)
    {
        Sparkle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowListeningElements(bool show)
    {
        ListeningWave.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StopAllAnimations()
    {
        foreach (var storyboard in Resources.Values)
        {
            if (storyboard is Storyboard sb)
            {
                sb.Stop(this);
            }
        }
    }

    private void BeginAnimation(string storyboardName)
    {
        if (Resources[storyboardName] is Storyboard storyboard)
        {
            storyboard.Begin(this, true);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
             
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isDragging = true;
                _dragStartPoint = e.GetPosition(null);
                _windowStartPoint = new Point(Left, Top);
                
                if (_currentState == SpriteState.Sleeping)
                {
                    CurrentState = SpriteState.Idle;
                }
                
                if (e.ClickCount == 2)
                {
                    SpriteClicked?.Invoke(this, EventArgs.Empty);
                }
            }
        }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
         
        if (_isDragging)
        {
            var currentPoint = e.GetPosition(null);
            var offset = new Point(currentPoint.X - _dragStartPoint.X, currentPoint.Y - _dragStartPoint.Y);

            Left =  Math.Max(0, Math.Min(System.Windows.SystemParameters.WorkArea.Width - Width, _windowStartPoint.X + offset.X));
            Top =   Math.Max(0, Math.Min(System.Windows.SystemParameters.WorkArea.Height - Height, _windowStartPoint.Y + offset.Y));
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            SavePosition();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_isDragging)
        {
            ShowInTaskbar = false;
        }
    }
    
    private void OnRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ContextMenuPopup.IsOpen = !ContextMenuPopup.IsOpen;
    }
    
    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
         
        if (_currentState == SpriteState.Sleeping)
        {
            CurrentState = SpriteState.Idle;
        }
        
        if (Resources["MouseOverAnimation"] is Storyboard storyboard)
        {
            storyboard.Begin(this);
        }
    }
    
    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (Resources["MouseLeaveAnimation"] is Storyboard storyboard)
        {
            storyboard.Begin(this);
        }
    }
    
    private void OpenChat_Click(object sender, RoutedEventArgs e)
    {
        ContextMenuPopup.IsOpen = false;
        SpriteClicked?.Invoke(this, EventArgs.Empty);
    }
    
    private void PlayMovie_Click(object sender, RoutedEventArgs e)
    {
        ContextMenuPopup.IsOpen = false;
        CurrentState = SpriteState.Working;
    }
    
    private void RecommendMovie_Click(object sender, RoutedEventArgs e)
    {
        ContextMenuPopup.IsOpen = false;
        CurrentState = SpriteState.Thinking;
    }
    
    private void HideSprite_Click(object sender, RoutedEventArgs e)
    {
        ContextMenuPopup.IsOpen = false;
        Close();
    }

    
}