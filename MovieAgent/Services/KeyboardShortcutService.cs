using MovieAgent.Core.Interfaces;
using System.Collections.Generic;
using System.Windows.Input;

namespace MovieAgent.Services;

public class KeyboardShortcutService : IKeyboardShortcutService
{
    private readonly Dictionary<KeyboardShortcut, Action> _handlers = new();

    public void RegisterShortcut(KeyboardShortcut shortcut, Action handler)
    {
        if (_handlers.ContainsKey(shortcut))
        {
            _handlers[shortcut] = handler;
        }
        else
        {
            _handlers.Add(shortcut, handler);
        }
    }

    public void UnregisterShortcut(KeyboardShortcut shortcut)
    {
        _handlers.Remove(shortcut);
    }

    public void HandleKeyDown(object e)
    {
        if (e is not System.Windows.Input.KeyEventArgs keyEventArgs)
            return;

        var isCtrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control;
        var isAlt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) == System.Windows.Input.ModifierKeys.Alt;
        var isShift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == System.Windows.Input.ModifierKeys.Shift;

        // Ctrl+数字键导航
        if (isCtrl && !isAlt && !isShift)
        {
            KeyboardShortcut? shortcut = null;
            switch (keyEventArgs.Key)
            {
                case System.Windows.Input.Key.D1:
                    shortcut = KeyboardShortcut.Home;
                    break;
                case System.Windows.Input.Key.D2:
                    shortcut = KeyboardShortcut.Dashboard;
                    break;
                case System.Windows.Input.Key.D3:
                    shortcut = KeyboardShortcut.Movies;
                    break;
                case System.Windows.Input.Key.D4:
                    shortcut = KeyboardShortcut.Search;
                    break;
                case System.Windows.Input.Key.D5:
                    shortcut = KeyboardShortcut.Chat;
                    break;
                case System.Windows.Input.Key.D6:
                    shortcut = KeyboardShortcut.Report;
                    break;
                case System.Windows.Input.Key.D7:
                    shortcut = KeyboardShortcut.Settings;
                    break;
            }

            if (shortcut.HasValue)
            {
                InvokeHandler(shortcut.Value);
                keyEventArgs.Handled = true;
                return;
            }
        }

        // Ctrl+F 搜索聚焦
        if (isCtrl && keyEventArgs.Key == System.Windows.Input.Key.F)
        {
            InvokeHandler(KeyboardShortcut.SearchFocus);
            keyEventArgs.Handled = true;
            return;
        }

        // Ctrl+R 刷新
        if (isCtrl && keyEventArgs.Key == System.Windows.Input.Key.R)
        {
            InvokeHandler(KeyboardShortcut.Refresh);
            keyEventArgs.Handled = true;
            return;
        }

        // Ctrl+T 切换主题
        if (isCtrl && keyEventArgs.Key == System.Windows.Input.Key.T)
        {
            InvokeHandler(KeyboardShortcut.ToggleTheme);
            keyEventArgs.Handled = true;
            return;
        }

        // ESC 退出
        if (keyEventArgs.Key == System.Windows.Input.Key.Escape)
        {
            InvokeHandler(KeyboardShortcut.Exit);
            keyEventArgs.Handled = true;
            return;
        }

        // 空格键播放/暂停
        if (keyEventArgs.Key == System.Windows.Input.Key.Space)
        {
            InvokeHandler(KeyboardShortcut.PlayPause);
            keyEventArgs.Handled = true;
            return;
        }

        // 方向键音量控制
        if (keyEventArgs.Key == System.Windows.Input.Key.Up && isCtrl)
        {
            InvokeHandler(KeyboardShortcut.VolumeUp);
            keyEventArgs.Handled = true;
            return;
        }

        if (keyEventArgs.Key == System.Windows.Input.Key.Down && isCtrl)
        {
            InvokeHandler(KeyboardShortcut.VolumeDown);
            keyEventArgs.Handled = true;
            return;
        }

        // M 键静音
        if (keyEventArgs.Key == System.Windows.Input.Key.M)
        {
            InvokeHandler(KeyboardShortcut.Mute);
            keyEventArgs.Handled = true;
            return;
        }

        // X 键停止
        if (keyEventArgs.Key == System.Windows.Input.Key.X)
        {
            InvokeHandler(KeyboardShortcut.Stop);
            keyEventArgs.Handled = true;
            return;
        }
    }

    private void InvokeHandler(KeyboardShortcut shortcut)
    {
        if (_handlers.TryGetValue(shortcut, out var handler))
        {
            try
            {
                handler.Invoke();
            }
            catch { }
        }
    }
}