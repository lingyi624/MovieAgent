using System.Windows;

namespace MovieAgent;

public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();
    }

    public void UpdateStatus(string status)
    {
        StatusText.Text = status;
    }
}