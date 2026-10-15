using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenTuyaSync.Service;

/// <summary>
/// Разрешает доступ к объекту ядра процессам с более низким уровнем
/// целостности.
///
/// Зачем это нужно. Служба Windows работает с высокой целостностью, и все
/// созданные ею объекты (включая именованный канал) по умолчанию доступны
/// только процессам не ниже этого уровня. Приложение, запущенное обычным
/// пользователем, получает отказ в доступе -- причём именно отказ, а не
/// «объект не найден». Это сильно сбивает с толку: канал существует, права
/// доступа его разрешают, а подключение не проходит.
///
/// Решение -- метка обязательного уровня (mandatory label) в системном
/// списке контроля доступа. Она описывает, процессам какого уровня
/// разрешено обращаться к объекту.
/// </summary>
internal static class MandatoryLabel
{
    private const int SeObjectTypeKernelObject = 6;
    private const int LabelSecurityInformation = 0x00000010;
    private const int UnprotectedSaclSecurityInformation = 0x10000000;

    /// <summary>ACL_REVISION_DS -- минимальная ревизия, понимающая метки.</summary>
    private const int AclRevision = 2;

    /// <summary>Флаги наследования для ACE метки.</summary>
    private const int InheritFlags = 0x03 | 0x04;

    /// <summary>Политика: запрещать запись в объекты более высокого уровня,
    /// но разрешать доступ к этому объекту снизу.</summary>
    private const int NoWriteUp = 0x1;

    /// <summary>WinLowLabelSid -- уровень «низкий»: доступ разрешён всем,
    /// кто выше него.</summary>
    private const int WinLowLabelSid = 66;

    [StructLayout(LayoutKind.Sequential)]
    private struct AclHeader
    {
        public byte AclRevision;
        public byte Sbz1;
        public ushort AclSize;
        public ushort AceCount;
        public ushort Sbz2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AceHeader
    {
        public byte AceType;
        public byte AceFlags;
        public ushort AceSize;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool InitializeAcl(IntPtr acl, int aclLength, int aclRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AddMandatoryAce(
        IntPtr acl, int aclRevision, int aceFlags, int mandatoryPolicy, IntPtr labelSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetSecurityInfo(
        SafeHandle handle, int objectType, int securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateWellKnownSid(
        int sidType, IntPtr domainSid, IntPtr sid, ref int cbSid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int GetLengthSid(IntPtr sid);

    // --- управление привилегией SeSecurityPrivilege ---

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>
    /// Включает привилегию SeSecurityPrivilege у текущего процесса.
    /// Без неё изменение системного списка доступа (метки целостности)
    /// отклоняется, и канал остаётся недоступным для обычных процессов.
    /// </summary>
    private static bool EnableSecurityPrivilege()
    {
        IntPtr token = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out token))
                return false;

            if (!LookupPrivilegeValue(null, "SeSecurityPrivilege", out var luid))
                return false;

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled,
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges возвращает true, даже когда привилегия
            // не назначена: это видно по последней ошибке.
            return Marshal.GetLastWin32Error() == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// Разрешает доступ к объекту процессам с более низким уровнем
    /// целостности. При неудаче возвращает false, не бросая исключение:
    /// канал останется рабочим для процессов того же уровня.
    /// </summary>
    public static bool AllowLowerIntegrity(SafeHandle handle)
    {
        IntPtr sidBuffer = IntPtr.Zero;
        IntPtr aclBuffer = IntPtr.Zero;

        try
        {
            EnableSecurityPrivilege();

            int sidLength = 32;
            sidBuffer = Marshal.AllocHGlobal(sidLength);

            if (!CreateWellKnownSid(WinLowLabelSid, IntPtr.Zero, sidBuffer, ref sidLength))
                return false;

            int sidSize = GetLengthSid(sidBuffer);
            if (sidSize <= 0) return false;

            int aclSize = Marshal.SizeOf<AclHeader>() + Marshal.SizeOf<AceHeader>() + sidSize;
            aclBuffer = Marshal.AllocHGlobal(aclSize);

            if (!InitializeAcl(aclBuffer, aclSize, AclRevision))
                return false;

            if (!AddMandatoryAce(aclBuffer, AclRevision, InheritFlags, NoWriteUp, sidBuffer))
                return false;

            return SetSecurityInfo(
                handle,
                SeObjectTypeKernelObject,
                LabelSecurityInformation | UnprotectedSaclSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, aclBuffer);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (sidBuffer != IntPtr.Zero) Marshal.FreeHGlobal(sidBuffer);
            if (aclBuffer != IntPtr.Zero) Marshal.FreeHGlobal(aclBuffer);
        }
    }
}
