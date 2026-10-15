using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenTuyaSync.Agent;

/// <summary>
/// Глобальные горячие клавиши через RegisterHotKey.
///
/// Регистрируются в потоке интерфейса: сообщение WM_HOTKEY приходит в
/// очередь потока, который вызвал RegisterHotKey, поэтому окно,
/// перехватывающее сообщения, должно жить в том же потоке.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _windowHandle;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager(IntPtr windowHandle) => _windowHandle = windowHandle;

    /// <summary>Список горячих клавиш, которые не удалось занять --
    /// их уже использует другая программа.</summary>
    public List<string> Failed { get; } = new();

    /// <summary>
    /// Регистрирует сочетание. Возвращает false, если оно занято другим
    /// приложением (это не ошибка конфигурации -- так бывает).
    /// </summary>
    public bool Register(int virtualKey, int modifiers, string name, Action action)
    {
        if (virtualKey == 0) return true;   // не задано -- пропускаем

        int id = _nextId++;
        if (!RegisterHotKey(_windowHandle, id, (uint)modifiers, (uint)virtualKey))
        {
            Failed.Add(name);
            return false;
        }

        _handlers[id] = action;
        return true;
    }

    /// <summary>Обрабатывает WM_HOTKEY. Вызывается из оконной процедуры.</summary>
    public bool HandleMessage(int message, IntPtr wParam)
    {
        if (message != WM_HOTKEY) return false;

        if (_handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            try { action(); } catch (Exception) { }
            return true;
        }

        return false;
    }

    public void UnregisterAll()
    {
        foreach (int id in _handlers.Keys)
        {
            try { UnregisterHotKey(_windowHandle, id); } catch (Exception) { }
        }
        _handlers.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
    }
}

/// <summary>
/// Автозапуск агента при входе пользователя в систему.
///
/// Используется ветка HKEY_CURRENT_USER: она не требует прав
/// администратора и действует только для текущего пользователя, что для
/// подсветки рабочего стола и нужно. Сама служба ставится отдельно и
/// стартует автоматически.
/// </summary>
public static class AutoStartManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenTuyaSync";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (string.IsNullOrEmpty(exe)) return;

                // --minimized: стартуем в трее, чтобы не мешать входу в систему.
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Отсутствие прав на реестр не должно ронять приложение.
        }
    }
}
