using System.ComponentModel;
using System.ServiceProcess;

namespace OpenTuyaSync.Agent;

/// <summary>
/// Запуск службы OpenTuya Sync из приложения.
///
/// Почему так. Раньше служба стартовала вместе с Windows (тип запуска
/// «автоматически»), то есть запуск выполняла служба. Нужен запуск
/// приложением, свёрнутым в трей: при входе в систему появляется приложение,
/// и оно поднимает службу. Из этого два следствия:
///
///   - служба ставится с типом запуска «вручную» и сама не стартует;
///   - приложение запускает её при старте, без запроса прав администратора.
///
/// Второе возможно потому, что при установке службе выдаётся разрешение на
/// запуск интерактивным пользователям.
///
/// Состояние службы читается через API Windows, а не разбором вывода sc.exe.
/// Причина: sc.exe печатает названия полей на языке системы («СОСТОЯНИЕ»
/// вместо «STATE»), из-за чего разбор текста не находил состояние и
/// приложение пыталось запустить уже работающую службу. Windows отвечала
/// «код 1056: служба уже запущена», и это показывалось как ошибка.
/// </summary>
public static class ServiceStarter
{
    public const string ServiceName = "OpenTuyaSync";

    /// <summary>Сколько ждать перехода службы в состояние «работает».</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Сколько ждать остановки, если служба как раз останавливается.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Запускает службу, если она установлена и остановлена.
    ///
    /// Возвращает текст для журнала. Ничего не бросает: недоступная служба
    /// не должна мешать запуску приложения -- Bluetooth-ленту обслуживает
    /// приложение, служба нужна только сетевым устройствам.
    /// </summary>
    public static string EnsureRunning()
    {
        ServiceController? service = null;

        try
        {
            service = new ServiceController(ServiceName);

            // Обращение к Status у неустановленной службы бросает
            // InvalidOperationException.
            var status = service.Status;

            if (status == ServiceControllerStatus.Running)
                return "Служба уже работала.";

            if (status == ServiceControllerStatus.StartPending)
            {
                service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
                return "Служба запущена.";
            }

            // Служба останавливается -- дождёмся остановки, иначе запуск
            // сразу после неё не пройдёт.
            if (status == ServiceControllerStatus.StopPending)
            {
                try { service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout); }
                catch (System.ServiceProcess.TimeoutException) { }
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);

            return "Служба запущена приложением.";
        }
        catch (InvalidOperationException)
        {
            // Служба не установлена. Это допустимо: ленту обслуживает
            // приложение, а сетевым устройствам нужна установка службы.
            return "Служба не установлена: сетевые устройства недоступны. " +
                   "Установите её на вкладке «Настройки».";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1056)
        {
            // 1056: служба уже запущена. Значит её успел запустить кто-то
            // ещё -- например, сама Windows сразу после установки. Это не
            // ошибка.
            return "Служба уже работала.";
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return "Служба запускается дольше обычного: проверьте состояние " +
                   "на вкладке «Настройки».";
        }
        catch (Exception ex)
        {
            return $"Службу запустить не удалось: {ex.Message}. " +
                   "Запустите ПЕРЕЗАПУСК-СЛУЖБЫ.bat от имени администратора.";
        }
        finally
        {
            service?.Dispose();
        }
    }

    /// <summary>
    /// Запускает службу в фоне и пишет итог в журнал приложения.
    ///
    /// Отдельный поток нужен потому, что ожидание готовности занимает
    /// секунды: интерфейс не должен ждать.
    /// </summary>
    public static void EnsureRunning(MainWindow window)
    {
        string result;

        try
        {
            result = EnsureRunning();
        }
        catch (Exception ex)
        {
            result = $"Службу запустить не удалось: {ex.Message}";
        }

        window.LogFromBackground(result);
    }
}
