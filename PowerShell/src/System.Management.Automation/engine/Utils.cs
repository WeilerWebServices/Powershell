// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management.Automation.Configuration;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Management.Automation.Remoting;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Security;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security;
#if !UNIX
using System.Security.Principal;
#endif
using System.Text;
using System.Threading;
using Microsoft.PowerShell.Commands;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

using TypeTable = System.Management.Automation.Runspaces.TypeTable;

namespace System.Management.Automation
{
    /// <summary>
    /// Helper fns.
    /// </summary>
    internal static class Utils
    {
        /// <summary>
        /// Converts a given double value to BigInteger via Math.Round().
        /// </summary>
        /// <param name="d">The value to convert.</param>
        /// <returns>Returns a BigInteger value equivalent to the input value rounded to nearest integer.</returns>
        internal static BigInteger AsBigInt(this double d) => new BigInteger(Math.Round(d));

        internal static bool TryCast(BigInteger value, out byte b)
        {
            if (value < byte.MinValue || byte.MaxValue < value)
            {
                b = 0;
                return false;
            }

            b = (byte)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out sbyte sb)
        {
            if (value < sbyte.MinValue || sbyte.MaxValue < value)
            {
                sb = 0;
                return false;
            }

            sb = (sbyte)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out short s)
        {
            if (value < short.MinValue || short.MaxValue < value)
            {
                s = 0;
                return false;
            }

            s = (short)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out ushort us)
        {
            if (value < ushort.MinValue || ushort.MaxValue < value)
            {
                us = 0;
                return false;
            }

            us = (ushort)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out int i)
        {
            if (value < int.MinValue || int.MaxValue < value)
            {
                i = 0;
                return false;
            }

            i = (int)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out uint u)
        {
            if (value < uint.MinValue || uint.MaxValue < value)
            {
                u = 0;
                return false;
            }

            u = (uint)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out long l)
        {
            if (value < long.MinValue || long.MaxValue < value)
            {
                l = 0;
                return false;
            }

            l = (long)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out ulong ul)
        {
            if (value < ulong.MinValue || ulong.MaxValue < value)
            {
                ul = 0;
                return false;
            }

            ul = (ulong)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out decimal dm)
        {
            if (value < (BigInteger)decimal.MinValue || (BigInteger)decimal.MaxValue < value)
            {
                dm = 0;
                return false;
            }

            dm = (decimal)value;
            return true;
        }

        internal static bool TryCast(BigInteger value, out double db)
        {
            if (value < (BigInteger)double.MinValue || (BigInteger)double.MaxValue < value)
            {
                db = 0;
                return false;
            }

            db = (double)value;
            return true;
        }

        /// <summary>
        /// Parses a given string or ReadOnlySpan&lt;char&gt; to calculate its value as a binary number.
        /// Assumes input has already been sanitized and only contains zeroes (0) or ones (1).
        /// </summary>
        /// <param name="digits">Span or string of binary digits. Assumes all digits are either 1 or 0.</param>
        /// <param name="unsigned">
        /// Whether to treat the number as unsigned. When false, respects established conventions
        /// with sign bits for certain input string lengths.
        /// </param>
        /// <returns>Returns the value of the binary string as a BigInteger.</returns>
        internal static BigInteger ParseBinary(ReadOnlySpan<char> digits, bool unsigned)
        {
            if (!unsigned)
            {
                if (digits[0] == '0')
                {
                    unsigned = true;
                }
                else
                {
                    switch (digits.Length)
                    {
                        // Only accept sign bits at these lengths:
                        case 8: // byte
                        case 16: // short
                        case 32: // int
                        case 64: // long
                        case 96: // decimal
                        case int n when n >= 128: // BigInteger
                            break;
                        default:
                            // If we do not flag these as unsigned, bigint assumes a sign bit for any (8 * n) string length
                            unsigned = true;
                            break;
                    }
                }
            }

            // Only use heap allocation for very large numbers
            const int MaxStackAllocation = 512;

            // Calculate number of 8-bit bytes needed to hold the input,  rounded up to next whole number.
            int outputByteCount = (digits.Length + 7) / 8;
            Span<byte> outputBytes = outputByteCount <= MaxStackAllocation ? stackalloc byte[outputByteCount] : new byte[outputByteCount];
            int outputByteIndex = outputBytes.Length - 1;

            // We need to be prepared for any partial leading bytes, (e.g., 010|00000011|00101100), or cases
            // where we only have less than 8 bits to work with from the beginning.
            //
            // Walk bytes right to left, stepping one whole byte at a time (if there are any whole bytes).
            int byteWalker;
            for (byteWalker = digits.Length - 1; byteWalker >= 7; byteWalker -= 8)
            {
                // Use bit shifts and binary-or to sum the values in each byte.  These calculations will
                // create values higher than a single byte, but the higher bits will be stripped out when cast
                // to byte.
                //
                // The low bits are added in separately to allow us to strip the higher 'noise' bits before we
                // sum the values using binary-or.
                //
                // Simplified representation of logic:     (byte)( (7)|(6)|(5)|(4) ) | ( ( (3)|(2)|(1)|(0) ) & 0b1111 )
                //
                // N.B.: This code has been tested against a straight for loop iterating through the byte, and in no
                // circumstance was it faster or more effective than this unrolled version.
                outputBytes[outputByteIndex--] =
                    (byte)(
                        ((digits[byteWalker - 7] << 7)
                        | (digits[byteWalker - 6] << 6)
                        | (digits[byteWalker - 5] << 5)
                        | (digits[byteWalker - 4] << 4)
                        )
                    | (
                        ((digits[byteWalker - 3] << 3)
                        | (digits[byteWalker - 2] << 2)
                        | (digits[byteWalker - 1] << 1)
                        | (digits[byteWalker])
                        ) & 0b1111
                      )
                    );
            }

            // With complete bytes parsed, byteWalker is either at the partial byte start index, or at -1
            if (byteWalker >= 0)
            {
                int currentByteValue = 0;
                for (int i = 0; i <= byteWalker; i++)
                {
                    currentByteValue = (currentByteValue << 1) | (digits[i] - '0');
                }

                outputBytes[outputByteIndex] = (byte)currentByteValue;
            }

            return new BigInteger(outputBytes, isUnsigned: unsigned, isBigEndian: true);
        }

        // From System.Web.Util.HashCodeCombiner
        internal static int CombineHashCodes(int h1, int h2)
        {
            return unchecked(((h1 << 5) + h1) ^ h2);
        }

        internal static int CombineHashCodes(int h1, int h2, int h3)
        {
            return CombineHashCodes(CombineHashCodes(h1, h2), h3);
        }

        internal static int CombineHashCodes(int h1, int h2, int h3, int h4)
        {
            return CombineHashCodes(CombineHashCodes(h1, h2), CombineHashCodes(h3, h4));
        }

        internal static int CombineHashCodes(int h1, int h2, int h3, int h4, int h5)
        {
            return CombineHashCodes(CombineHashCodes(h1, h2, h3, h4), h5);
        }

        internal static int CombineHashCodes(int h1, int h2, int h3, int h4, int h5, int h6)
        {
            return CombineHashCodes(CombineHashCodes(h1, h2, h3, h4), CombineHashCodes(h5, h6));
        }

        internal static int CombineHashCodes(int h1, int h2, int h3, int h4, int h5, int h6, int h7)
        {
            return CombineHashCodes(CombineHashCodes(h1, h2, h3, h4), CombineHashCodes(h5, h6, h7));
        }

        internal static int CombineHashCodes(int h1, int h2, int h3, int h4, int h5, int h6, int h7, int h8)
        {
            return CombineHashCodes(CombineHashCodes(h1, h2, h3, h4), CombineHashCodes(h5, h6, h7, h8));
        }

        /// <summary>
        /// Allowed PowerShell Editions.
        /// </summary>
        internal static readonly string[] AllowedEditionValues = { "Desktop", "Core" };

        /// <summary>
        /// Helper fn to check byte[] arg for null.
        /// </summary>
        ///<param name="arg"> arg to check </param>
        ///<param name="argName"> name of the arg </param>
        ///<returns> Does not return a value.</returns>
        internal static void CheckKeyArg(byte[] arg, string argName)
        {
            if (arg == null)
            {
                throw PSTraceSource.NewArgumentNullException(argName);
            }
            //
            // we use AES algorithm which supports key
            // lengths of 128, 192 and 256 bits.
            // We throw ArgumentException if the key is
            // of any other length
            //
            else if (!((arg.Length == 16) ||
                       (arg.Length == 24) ||
                       (arg.Length == 32)))
            {
                throw PSTraceSource.NewArgumentException(argName, Serialization.InvalidKeyLength, argName);
            }
        }

        /// <summary>
        /// Helper fn to check arg for empty or null.
        /// Throws ArgumentNullException on either condition.
        /// </summary>
        ///<param name="arg"> arg to check </param>
        ///<param name="argName"> name of the arg </param>
        ///<returns> Does not return a value.</returns>
        internal static void CheckArgForNullOrEmpty(string arg, string argName)
        {
            if (arg == null)
            {
                throw PSTraceSource.NewArgumentNullException(argName);
            }
            else if (arg.Length == 0)
            {
                throw PSTraceSource.NewArgumentException(argName);
            }
        }

        /// <summary>
        /// Helper fn to check arg for null.
        /// Throws ArgumentNullException on either condition.
        /// </summary>
        ///<param name="arg"> arg to check </param>
        ///<param name="argName"> name of the arg </param>
        ///<returns> Does not return a value.</returns>
        internal static void CheckArgForNull(object arg, string argName)
        {
            if (arg == null)
            {
                throw PSTraceSource.NewArgumentNullException(argName);
            }
        }

        /// <summary>
        /// Helper fn to check arg for null.
        /// </summary>
        ///<param name="arg"> arg to check </param>
        ///<param name="argName"> name of the arg </param>
        ///<returns> Does not return a value.</returns>
        internal static void CheckSecureStringArg(SecureString arg, string argName)
        {
            if (arg == null)
            {
                throw PSTraceSource.NewArgumentNullException(argName);
            }
        }

        [ArchitectureSensitive]
        internal static string GetStringFromSecureString(SecureString ss)
        {
            IntPtr p = IntPtr.Zero;
            string s = null;

            try
            {
                p = Marshal.SecureStringToCoTaskMemUnicode(ss);
                s = Marshal.PtrToStringUni(p);
            }
            finally
            {
                if (p != IntPtr.Zero)
                {
                    Marshal.ZeroFreeCoTaskMemUnicode(p);
                }
            }

            return s;
        }

        /// <summary>
        /// Gets TypeTable by querying the ExecutionContext stored in
        /// Thread-Local-Storage. This will return null if ExecutionContext
        /// is not available.
        /// </summary>
        /// <returns></returns>
        internal static TypeTable GetTypeTableFromExecutionContextTLS()
        {
            ExecutionContext ecFromTLS = Runspaces.LocalPipeline.GetExecutionContextFromTLS();
            if (ecFromTLS == null)
            {
                return null;
            }

            return ecFromTLS.TypeTable;
        }

#if !UNIX
        private static string s_pshome = null;

        /// <summary>
        /// Get the application base path of the shell from registry.
        /// </summary>
        internal static string GetApplicationBaseFromRegistry(string shellId)
        {
            bool wantPsHome = (object)shellId == (object)DefaultPowerShellShellID;
            if (wantPsHome && s_pshome != null)
                return s_pshome;

            string engineKeyPath = RegistryStrings.MonadRootKeyPath + "\\" +
                PSVersionInfo.RegistryVersionKey + "\\" + RegistryStrings.MonadEngineKey;

            using (RegistryKey engineKey = Registry.LocalMachine.OpenSubKey(engineKeyPath))
            {
                if (engineKey != null)
                {
                    var result = engineKey.GetValue(RegistryStrings.MonadEngine_ApplicationBase) as string;
                    result = Environment.ExpandEnvironmentVariables(result);
                    if (wantPsHome)
                        Interlocked.CompareExchange(ref s_pshome, null, result);

                    return result;
                }
            }

            return null;
        }

        private static string s_windowsPowerShellVersion = null;

        /// <summary>
        /// Get the Windows PowerShell version from registry.
        /// </summary>
        /// <returns>
        /// String of Windows PowerShell version from registry.
        /// </returns>
        internal static string GetWindowsPowerShellVersionFromRegistry()
        {
            if (!string.IsNullOrEmpty(InternalTestHooks.TestWindowsPowerShellVersionString))
            {
                return InternalTestHooks.TestWindowsPowerShellVersionString;
            }

            if (s_windowsPowerShellVersion != null)
            {
                return s_windowsPowerShellVersion;
            }

            string engineKeyPath = RegistryStrings.MonadRootKeyPath + "\\" +
                PSVersionInfo.RegistryVersionKey + "\\" + RegistryStrings.MonadEngineKey;

            using (RegistryKey engineKey = Registry.LocalMachine.OpenSubKey(engineKeyPath))
            {
                if (engineKey != null)
                {
                    s_windowsPowerShellVersion = engineKey.GetValue(RegistryStrings.MonadEngine_MonadVersion) as string;
                    return s_windowsPowerShellVersion;
                }
            }

            return string.Empty;
        }
#endif

        internal static string DefaultPowerShellAppBase => GetApplicationBase(DefaultPowerShellShellID);

        internal static string GetApplicationBase(string shellId)
        {
            // Use the location of SMA.dll as the application base.
            Assembly assembly = typeof(PSObject).Assembly;
            return Path.GetDirectoryName(assembly.Location);
        }

        private static string[] s_productFolderDirectories;

        private static string[] GetProductFolderDirectories()
        {
            if (s_productFolderDirectories == null)
            {
                List<string> baseDirectories = new List<string>();

                // Retrieve the application base from the registry
                string appBase = Utils.DefaultPowerShellAppBase;
                if (!string.IsNullOrEmpty(appBase))
                {
                    baseDirectories.Add(appBase);
                }
#if !UNIX
                // Now add the two variations of System32
                baseDirectories.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
                string systemX86 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
                if (!string.IsNullOrEmpty(systemX86))
                {
                    baseDirectories.Add(systemX86);
                }
#endif
                Interlocked.CompareExchange(ref s_productFolderDirectories, baseDirectories.ToArray(), null);
            }

            return s_productFolderDirectories;
        }

        /// <summary>
        /// Checks if the filePath represents a file under product folder
        /// ie., PowerShell ApplicationBase or $env:windir\system32 or
        /// $env:windir\syswow64.
        /// </summary>
        /// <returns>
        /// true: if the filePath is under product folder
        /// false: otherwise
        /// </returns>
        internal static bool IsUnderProductFolder(string filePath)
        {
            FileInfo fileInfo = new FileInfo(filePath);
            string filename = fileInfo.FullName;

            var productFolderDirectories = GetProductFolderDirectories();
            for (int i = 0; i < productFolderDirectories.Length; i++)
            {
                string applicationBase = productFolderDirectories[i];
                if (filename.StartsWith(applicationBase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the current process is using WOW.
        /// </summary>
        internal static bool IsRunningFromSysWOW64()
        {
            return DefaultPowerShellAppBase.Contains("SysWOW64");
        }

        /// <summary>
        /// Checks if host machine is WinPE.
        /// </summary>
        internal static bool IsWinPEHost()
        {
#if !UNIX
            RegistryKey winPEKey = null;

            try
            {
                // The existence of the following registry confirms that the host machine is a WinPE
                // HKLM\System\CurrentControlSet\Control\MiniNT
                winPEKey = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\MiniNT");

                return winPEKey != null;
            }
            catch (ArgumentException) { }
            catch (SecurityException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (winPEKey != null)
                {
                    winPEKey.Dispose();
                }
            }
#endif
            return false;
        }

        #region Versioning related methods

        /// <summary>
        /// Returns current major version of monad ( that is running ) in a string
        /// format.
        /// </summary>
        /// <returns>String.</returns>
        /// <remarks>
        /// Cannot return a Version object as minor number is a requirement for
        /// version object.
        /// </remarks>
        internal static string GetCurrentMajorVersion()
        {
            return PSVersionInfo.PSVersion.Major.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Coverts a string to version format.
        /// If the string is of the format x (ie., no dots), then ".0" is appended
        /// to the string.
        /// Version.TryParse will be used to convert the string to a Version
        /// object.
        /// </summary>
        /// <param name="versionString">String representing version.</param>
        /// <returns>A Version Object.</returns>
        internal static Version StringToVersion(string versionString)
        {
            // max of 1 dot is allowed in version
            if (string.IsNullOrEmpty(versionString))
            {
                return null;
            }

            int dotCount = 0;
            foreach (char c in versionString)
            {
                if (c == '.')
                {
                    dotCount++;
                    if (dotCount > 1)
                    {
                        break;
                    }
                }
            }
            // Version.TryParse expects the string to be in format: major.minor[.build[.revision]]
            if (dotCount == 0)
            {
                versionString += ".0";
            }

            Version result = null;
            if (Version.TryParse(versionString, out result))
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// Checks whether current monad session supports version specified
        /// by ver.
        /// </summary>
        /// <param name="ver">Version to check.</param>
        /// <returns>True if supported, false otherwise.</returns>
        internal static bool IsPSVersionSupported(string ver)
        {
            // Convert version to supported format ie., x.x
            Version inputVersion = StringToVersion(ver);
            return IsPSVersionSupported(inputVersion);
        }

        /// <summary>
        /// Checks whether current monad session supports version specified
        /// by checkVersion.
        /// </summary>
        /// <param name="checkVersion">Version to check.</param>
        /// <returns>True if supported, false otherwise.</returns>
        internal static bool IsPSVersionSupported(Version checkVersion)
        {
            if (checkVersion == null)
            {
                return false;
            }

            foreach (Version compatibleVersion in PSVersionInfo.PSCompatibleVersions)
            {
                if (checkVersion.Major == compatibleVersion.Major && checkVersion.Minor <= compatibleVersion.Minor)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether current PowerShell session supports edition specified
        /// by checkEdition.
        /// </summary>
        /// <param name="checkEdition">Edition to check.</param>
        /// <returns>True if supported, false otherwise.</returns>
        internal static bool IsPSEditionSupported(string checkEdition)
        {
            return PSVersionInfo.PSEdition.Equals(checkEdition, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Check whether the current PowerShell session supports any of the specified editions.
        /// </summary>
        /// <param name="editions">The PowerShell editions to check compatibility with.</param>
        /// <returns>True if the edition is supported by this runtime, false otherwise.</returns>
        internal static bool IsPSEditionSupported(IEnumerable<string> editions)
        {
            string currentPSEdition = PSVersionInfo.PSEdition;
            foreach (string edition in editions)
            {
                if (currentPSEdition.Equals(edition, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether the specified edition value is allowed.
        /// </summary>
        /// <param name="editionValue">Edition value to check.</param>
        /// <returns>True if allowed, false otherwise.</returns>
        internal static bool IsValidPSEditionValue(string editionValue)
        {
            return AllowedEditionValues.Contains(editionValue, StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        /// <summary>
        /// String representing the Default shellID.
        /// </summary>
        internal const string DefaultPowerShellShellID = "Microsoft.PowerShell";

        /// <summary>
        /// This is used to construct the profile path.
        /// </summary>
        internal const string ProductNameForDirectory = "PowerShell";

        /// <summary>
        /// The subdirectory of module paths
        /// e.g. ~\Documents\WindowsPowerShell\Modules and %ProgramFiles%\WindowsPowerShell\Modules.
        /// </summary>
        internal static readonly string ModuleDirectory = Path.Combine(ProductNameForDirectory, "Modules");

        internal static readonly ConfigScope[] SystemWideOnlyConfig = new[] { ConfigScope.AllUsers };
        internal static readonly ConfigScope[] CurrentUserOnlyConfig = new[] { ConfigScope.CurrentUser };
        internal static readonly ConfigScope[] SystemWideThenCurrentUserConfig = new[] { ConfigScope.AllUsers, ConfigScope.CurrentUser };
        internal static readonly ConfigScope[] CurrentUserThenSystemWideConfig = new[] { ConfigScope.CurrentUser, ConfigScope.AllUsers };

        internal static T GetPolicySetting<T>(ConfigScope[] preferenceOrder) where T : PolicyBase, new()
        {
            T policy = null;
#if !UNIX
            // On Windows, group policy settings from registry take precedence.
            // If the requested policy is not defined in registry, we query the configuration file.
            policy = GetPolicySettingFromGPO<T>(preferenceOrder);
            if (policy != null) { return policy; }
#endif
            policy = GetPolicySettingFromConfigFile<T>(preferenceOrder);
            return policy;
        }

        private static readonly ConcurrentDictionary<ConfigScope, PowerShellPolicies> s_cachedPoliciesFromConfigFile =
            new ConcurrentDictionary<ConfigScope, PowerShellPolicies>();

        /// <summary>
        /// Get a specific kind of policy setting from the configuration file.
        /// </summary>
        private static T GetPolicySettingFromConfigFile<T>(ConfigScope[] preferenceOrder) where T : PolicyBase, new()
        {
            foreach (ConfigScope scope in preferenceOrder)
            {
                PowerShellPolicies policies;
                if (InternalTestHooks.BypassGroupPolicyCaching)
                {
                    policies = PowerShellConfig.Instance.GetPowerShellPolicies(scope);
                }
                else if (!s_cachedPoliciesFromConfigFile.TryGetValue(scope, out policies))
                {
                    // Use lock here to reduce the contention on accessing the configuration file
                    lock (s_cachedPoliciesFromConfigFile)
                    {
                        policies = s_cachedPoliciesFromConfigFile.GetOrAdd(scope, PowerShellConfig.Instance.GetPowerShellPolicies);
                    }
                }

                if (policies != null)
                {
                    PolicyBase result = null;
                    switch (typeof(T).Name)
                    {
                        case nameof(ScriptExecution):
                            result = policies.ScriptExecution;
                            break;
                        case nameof(ScriptBlockLogging):
                            result = policies.ScriptBlockLogging;
                            break;
                        case nameof(ModuleLogging):
                            result = policies.ModuleLogging;
                            break;
                        case nameof(ProtectedEventLogging):
                            result = policies.ProtectedEventLogging;
                            break;
                        case nameof(Transcription):
                            result = policies.Transcription;
                            break;
                        case nameof(UpdatableHelp):
                            result = policies.UpdatableHelp;
                            break;
                        case nameof(ConsoleSessionConfiguration):
                            result = policies.ConsoleSessionConfiguration;
                            break;
                        default:
                            Diagnostics.Assert(false, "Should be unreachable code. Update this switch block when new PowerShell policy types are added.");
                            break;
                    }

                    if (result != null) { return (T)result; }
                }
            }

            return null;
        }

#if !UNIX
        private static readonly Dictionary<string, string> GroupPolicyKeys = new Dictionary<string, string>
        {
            {nameof(ScriptExecution), @"Software\Policies\Microsoft\PowerShellCore"},
            {nameof(ScriptBlockLogging), @"Software\Policies\Microsoft\PowerShellCore\ScriptBlockLogging"},
            {nameof(ModuleLogging), @"Software\Policies\Microsoft\PowerShellCore\ModuleLogging"},
            {nameof(ProtectedEventLogging), @"Software\Policies\Microsoft\Windows\EventLog\ProtectedEventLogging"},
            {nameof(Transcription), @"Software\Policies\Microsoft\PowerShellCore\Transcription"},
            {nameof(UpdatableHelp), @"Software\Policies\Microsoft\PowerShellCore\UpdatableHelp"},
            {nameof(ConsoleSessionConfiguration), @"Software\Policies\Microsoft\PowerShellCore\ConsoleSessionConfiguration"}
        };

        private static readonly Dictionary<string, string> WindowsPowershellGroupPolicyKeys = new Dictionary<string, string>
        {
            { nameof(ScriptExecution), @"Software\Policies\Microsoft\Windows\PowerShell" },
            { nameof(ScriptBlockLogging), @"Software\Policies\Microsoft\Windows\PowerShell\ScriptBlockLogging" },
            { nameof(ModuleLogging), @"Software\Policies\Microsoft\Windows\PowerShell\ModuleLogging" },
            { nameof(Transcription), @"Software\Policies\Microsoft\Windows\PowerShell\Transcription" },
            { nameof(UpdatableHelp), @"Software\Policies\Microsoft\Windows\PowerShell\UpdatableHelp" },
        };

        private const string PolicySettingFallbackKey = "UseWindowsPowerShellPolicySetting";

        private static readonly ConcurrentDictionary<ConfigScope, ConcurrentDictionary<string, PolicyBase>> s_cachedPoliciesFromRegistry =
            new ConcurrentDictionary<ConfigScope, ConcurrentDictionary<string, PolicyBase>>();

        private static readonly Func<ConfigScope, ConcurrentDictionary<string, PolicyBase>> s_subCacheCreationDelegate =
            key => new ConcurrentDictionary<string, PolicyBase>(StringComparer.Ordinal);

        /// <summary>
        /// Read policy settings from a registry key into a policy object.
        /// </summary>
        /// <param name="instance">Policy object that will be filled with values from registry.</param>
        /// <param name="instanceType">Type of policy object used.</param>
        /// <param name="gpoKey">Registry key that has policy settings.</param>
        /// <returns>True if any property was successfully set on the policy object.</returns>
        private static bool TrySetPolicySettingsFromRegistryKey(object instance, Type instanceType, RegistryKey gpoKey)
        {
            var properties = instanceType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
            bool isAnyPropertySet = false;

            string[] valueNames = gpoKey.GetValueNames();
            string[] subKeyNames = gpoKey.GetSubKeyNames();
            var valueNameSet = valueNames.Length > 0 ? new HashSet<string>(valueNames, StringComparer.OrdinalIgnoreCase) : null;
            var subKeyNameSet = subKeyNames.Length > 0 ? new HashSet<string>(subKeyNames, StringComparer.OrdinalIgnoreCase) : null;

            // If there are any values or subkeys in the registry key - read them into the policy instance object
            if ((valueNameSet != null) || (subKeyNameSet != null))
            {
                foreach (var property in properties)
                {
                    string settingName = property.Name;
                    object rawRegistryValue = null;

                    // Get the raw value from registry.
                    if (valueNameSet != null && valueNameSet.Contains(settingName))
                    {
                        rawRegistryValue = gpoKey.GetValue(settingName);
                    }
                    else if (subKeyNameSet != null && subKeyNameSet.Contains(settingName))
                    {
                        using (RegistryKey subKey = gpoKey.OpenSubKey(settingName))
                        {
                            if (subKey != null)
                            {
                                rawRegistryValue = subKey.GetValueNames();
                            }
                        }
                    }

                    // Get the actual property value based on the property type.
                    // If the final property value is not null, then set the property.
                    if (rawRegistryValue != null)
                    {
                        Type propertyType = property.PropertyType;
                        object propertyValue = null;

                        switch (propertyType)
                        {
                            case var _ when propertyType == typeof(bool?):
                                if (rawRegistryValue is int rawIntValue)
                                {
                                    if (rawIntValue == 1)
                                    {
                                        propertyValue = true;
                                    }
                                    else if (rawIntValue == 0)
                                    {
                                        propertyValue = false;
                                    }
                                }

                                break;
                            case var _ when propertyType == typeof(string):
                                if (rawRegistryValue is string rawStringValue)
                                {
                                    propertyValue = rawStringValue;
                                }

                                break;
                            case var _ when propertyType == typeof(string[]):
                                if (rawRegistryValue is string[] rawStringArrayValue)
                                {
                                    propertyValue = rawStringArrayValue;
                                }
                                else if (rawRegistryValue is string stringValue)
                                {
                                    propertyValue = new string[] { stringValue };
                                }

                                break;
                            default:
                                throw System.Management.Automation.Interpreter.Assert.Unreachable;
                        }

                        // Set the property if the value is not null
                        if (propertyValue != null)
                        {
                            property.SetValue(instance, propertyValue);
                            isAnyPropertySet = true;
                        }
                    }
                }
            }

            return isAnyPropertySet;
        }

        /// <summary>
        /// The implementation of fetching a specific kind of policy setting from the given configuration scope.
        /// </summary>
        private static T GetPolicySettingFromGPOImpl<T>(ConfigScope scope) where T : PolicyBase, new()
        {
            Type tType = typeof(T);
            // SystemWide scope means 'LocalMachine' root key when query from registry
            RegistryKey rootKey = (scope == ConfigScope.AllUsers) ? Registry.LocalMachine : Registry.CurrentUser;

            GroupPolicyKeys.TryGetValue(tType.Name, out string gpoKeyPath);
            Diagnostics.Assert(gpoKeyPath != null, StringUtil.Format("The GPO registry key path should be pre-defined for {0}", tType.Name));

            using (RegistryKey gpoKey = rootKey.OpenSubKey(gpoKeyPath))
            {
                // If the corresponding GPO key doesn't exist, return null
                if (gpoKey == null) { return null; }

                // The corresponding GPO key exists, then create an instance of T
                // and populate its properties with the settings
                object tInstance = Activator.CreateInstance(tType, nonPublic: true);
                bool isAnyPropertySet = false;

                // if PolicySettingFallbackKey is Not set - use PowerShell Core policy reg key
                if ((int)gpoKey.GetValue(PolicySettingFallbackKey, 0) == 0)
                {
                    isAnyPropertySet = TrySetPolicySettingsFromRegistryKey(tInstance, tType, gpoKey);
                }
                else
                {
                    // when PolicySettingFallbackKey flag is set (REG_DWORD "1") use Windows PS policy reg key
                    WindowsPowershellGroupPolicyKeys.TryGetValue(tType.Name, out string winPowershellGpoKeyPath);
                    Diagnostics.Assert(winPowershellGpoKeyPath != null, StringUtil.Format("The Windows PS GPO registry key path should be pre-defined for {0}", tType.Name));
                    using (RegistryKey winPowershellGpoKey = rootKey.OpenSubKey(winPowershellGpoKeyPath))
                    {
                        // If the corresponding Windows PS GPO key doesn't exist, return null
                        if (winPowershellGpoKey == null) { return null; }
                        isAnyPropertySet = TrySetPolicySettingsFromRegistryKey(tInstance, tType, winPowershellGpoKey);
                    }
                }

                // If no property is set, then we consider this policy as undefined
                return isAnyPropertySet ? (T)tInstance : null;
            }
        }

        /// <summary>
        /// Get a specific kind of policy setting from the group policy registry key.
        /// </summary>
        private static T GetPolicySettingFromGPO<T>(ConfigScope[] preferenceOrder) where T : PolicyBase, new()
        {
            PolicyBase policy = null;
            string policyName = typeof(T).Name;

            foreach (ConfigScope scope in preferenceOrder)
            {
                if (InternalTestHooks.BypassGroupPolicyCaching)
                {
                    policy = GetPolicySettingFromGPOImpl<T>(scope);
                }
                else
                {
                    var subordinateCache = s_cachedPoliciesFromRegistry.GetOrAdd(scope, s_subCacheCreationDelegate);
                    if (!subordinateCache.TryGetValue(policyName, out policy))
                    {
                        policy = subordinateCache.GetOrAdd(policyName, key => GetPolicySettingFromGPOImpl<T>(scope));
                    }
                }

                if (policy != null) { return (T)policy; }
            }

            return null;
        }
#endif

        /// <summary>
        /// Scheduled job module name.
        /// </summary>
        internal const string ScheduledJobModuleName = "PSScheduledJob";

        internal static void EnsureModuleLoaded(string module, ExecutionContext context)
        {
            if (context != null && !context.AutoLoadingModuleInProgress.Contains(module))
            {
                List<PSModuleInfo> loadedModules = context.Modules.GetModules(new string[] { module }, false);

                if ((loadedModules == null) || (loadedModules.Count == 0))
                {
                    CommandInfo commandInfo = new CmdletInfo("Import-Module", typeof(Microsoft.PowerShell.Commands.ImportModuleCommand),
                                                             null, null, context);
                    var importModuleCommand = new System.Management.Automation.Runspaces.Command(commandInfo);

                    context.AutoLoadingModuleInProgress.Add(module);

                    PowerShell ps = null;

                    try
                    {
                        ps = PowerShell.Create(RunspaceMode.CurrentRunspace)
                            .AddCommand(importModuleCommand)
                            .AddParameter("Name", module)
                            .AddParameter("Scope", StringLiterals.Global)
                            .AddParameter("ErrorAction", ActionPreference.Ignore)
                            .AddParameter("WarningAction", ActionPreference.Ignore)
                            .AddParameter("InformationAction", ActionPreference.Ignore)
                            .AddParameter("Verbose", false)
                            .AddParameter("Debug", false)
                            .AddParameter("PassThru");

                        ps.Invoke<PSModuleInfo>();
                    }
                    catch (Exception)
                    {
                        // Call-out to user code, catch-all OK
                    }
                    finally
                    {
                        context.AutoLoadingModuleInProgress.Remove(module);
                        if (ps != null)
                        {
                            ps.Dispose();
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Returns modules (either loaded or in available) that match pattern <paramref name="module"/>.
        /// Uses Get-Module -ListAvailable cmdlet.
        /// </summary>
        /// <param name="module"></param>
        /// <param name="context"></param>
        /// <returns>
        /// List of PSModuleInfo's or Null.
        /// </returns>
        internal static List<PSModuleInfo> GetModules(string module, ExecutionContext context)
        {
            // first look in the loaded modules and then append the modules from gmo -Listavailable
            // Reason: gmo -li looks only the PSModulepath. There may be cases where a module
            // is imported directly from a path (that is not in PSModulePath).
            List<PSModuleInfo> result = context.Modules.GetModules(new string[] { module }, false);

            CommandInfo commandInfo = new CmdletInfo("Get-Module", typeof(Microsoft.PowerShell.Commands.GetModuleCommand),
                                                     null, null, context);
            var getModuleCommand = new System.Management.Automation.Runspaces.Command(commandInfo);

            PowerShell ps = null;
            try
            {
                ps = PowerShell.Create(RunspaceMode.CurrentRunspace)
                        .AddCommand(getModuleCommand)
                        .AddParameter("Name", module)
                        .AddParameter("ErrorAction", ActionPreference.Ignore)
                        .AddParameter("WarningAction", ActionPreference.Ignore)
                        .AddParameter("Verbose", false)
                        .AddParameter("Debug", false)
                        .AddParameter("ListAvailable");

                Collection<PSModuleInfo> gmoOutPut = ps.Invoke<PSModuleInfo>();
                if (gmoOutPut != null)
                {
                    if (result == null)
                    {
                        result = gmoOutPut.ToList<PSModuleInfo>();
                    }
                    else
                    {
                        result.AddRange(gmoOutPut);
                    }
                }
            }
            catch (Exception)
            {
                // Call-out to user code, catch-all OK
            }
            finally
            {
                if (ps != null)
                {
                    ps.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// Returns modules (either loaded or in available) that match FullyQualifiedName <paramref name="fullyQualifiedName"/>.
        /// Uses Get-Module -ListAvailable cmdlet.
        /// </summary>
        /// <param name="fullyQualifiedName"></param>
        /// <param name="context"></param>
        /// <returns>
        /// List of PSModuleInfo's or Null.
        /// </returns>
        internal static List<PSModuleInfo> GetModules(ModuleSpecification fullyQualifiedName, ExecutionContext context)
        {
            // first look in the loaded modules and then append the modules from gmo -Listavailable
            // Reason: gmo -li looks only the PSModulepath. There may be cases where a module
            // is imported directly from a path (that is not in PSModulePath).
            List<PSModuleInfo> result = context.Modules.GetModules(new[] { fullyQualifiedName }, false);
            CommandInfo commandInfo = new CmdletInfo("Get-Module", typeof(GetModuleCommand),
                                                     null, null, context);
            var getModuleCommand = new Runspaces.Command(commandInfo);

            PowerShell ps = null;
            try
            {
                ps = PowerShell.Create(RunspaceMode.CurrentRunspace)
                        .AddCommand(getModuleCommand)
                        .AddParameter("FullyQualifiedName", fullyQualifiedName)
                        .AddParameter("ErrorAction", ActionPreference.Ignore)
                        .AddParameter("WarningAction", ActionPreference.Ignore)
                        .AddParameter("InformationAction", ActionPreference.Ignore)
                        .AddParameter("Verbose", false)
                        .AddParameter("Debug", false)
                        .AddParameter("ListAvailable");

                Collection<PSModuleInfo> gmoOutput = ps.Invoke<PSModuleInfo>();
                if (gmoOutput != null)
                {
                    if (result == null)
                    {
                        result = gmoOutput.ToList();
                    }
                    else
                    {
                        // append to result
                        result.AddRange(gmoOutput);
                    }
                }
            }
            catch (Exception)
            {
                // Call-out to user code, catch-all OK
            }
            finally
            {
                if (ps != null)
                {
                    ps.Dispose();
                }
            }

            return result;
        }

#if !UNIX
        private static bool TryGetWindowsCurrentIdentity(out WindowsIdentity currentIdentity)
        {
            try
            {
                currentIdentity = WindowsIdentity.GetCurrent();
            }
            catch (SecurityException)
            {
                currentIdentity = null;
            }

            return (currentIdentity != null);
        }

        /// <summary>
        /// Gets the current impersonating Windows identity, if any.
        /// </summary>
        /// <param name="impersonatedIdentity">Current impersonated Windows identity or null.</param>
        /// <returns>True if current identity is impersonated.</returns>
        internal static bool TryGetWindowsImpersonatedIdentity(out WindowsIdentity impersonatedIdentity)
        {
            WindowsIdentity currentIdentity;
            if (TryGetWindowsCurrentIdentity(out currentIdentity) && (currentIdentity.ImpersonationLevel == TokenImpersonationLevel.Impersonation))
            {
                impersonatedIdentity = currentIdentity;
                return true;
            }

            impersonatedIdentity = null;
            return false;
        }
#endif

        internal static bool IsAdministrator()
        {
            // Porting note: only Windows supports the SecurityPrincipal API of .NET. Due to
            // advanced privilege models, the correct approach on Unix is to assume the user has
            // permissions, attempt the task, and error gracefully if the task fails due to
            // permissions. To fit into PowerShell's existing model of preemptively checking
            // permissions (which cannot be assumed on Unix), we "assume" the user is an
            // administrator by returning true, thus nullifying this check on Unix.
#if UNIX
            return true;
#else
            WindowsIdentity currentIdentity;
            if (TryGetWindowsCurrentIdentity(out currentIdentity))
            {
                var principal = new WindowsPrincipal(currentIdentity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }

            return false;
#endif
        }

        internal static bool IsReservedDeviceName(string destinationPath)
        {
#if !UNIX
            string[] reservedDeviceNames = { "CON", "PRN", "AUX", "CLOCK$", "NUL",
                                             "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                             "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
            string compareName = Path.GetFileName(destinationPath);
            string noExtensionCompareName = Path.GetFileNameWithoutExtension(destinationPath);

            if (((compareName.Length < 3) || (compareName.Length > 6)) &&
                ((noExtensionCompareName.Length < 3) || (noExtensionCompareName.Length > 6)))
            {
                return false;
            }

            foreach (string deviceName in reservedDeviceNames)
            {
                if (
                    string.Equals(deviceName, compareName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(deviceName, noExtensionCompareName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
#endif
            return false;
        }

        internal static bool PathIsUnc(string path)
        {
#if UNIX
            return false;
#else
            if (string.IsNullOrEmpty(path) || !path.StartsWith('\\'))
            {
                return false;
            }

            // handle special cases like \\wsl$\ubuntu which isn't a UNC path, but we can say it is so the filesystemprovider can use it
            if (path.StartsWith(@"\\wsl$", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Uri uri;
            return Uri.TryCreate(path, UriKind.Absolute, out uri) && uri.IsUnc;
#endif
        }

        internal static readonly string PowerShellAssemblyStrongNameFormat =
            "{0}, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35";

        internal static readonly HashSet<string> PowerShellAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "microsoft.powershell.commands.diagnostics",
                    "microsoft.powershell.commands.management",
                    "microsoft.powershell.commands.utility",
                    "microsoft.powershell.consolehost",
                    "microsoft.powershell.scheduledjob",
                    "microsoft.powershell.security",
                    "microsoft.wsman.management",
                    "microsoft.wsman.runtime",
                    "system.management.automation"
                };

        internal static bool IsPowerShellAssembly(string assemblyName)
        {
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                // Remove the '.dll' if it's there...
                var fixedName = assemblyName.EndsWith(StringLiterals.PowerShellILAssemblyExtension, StringComparison.OrdinalIgnoreCase)
                                ? Path.GetFileNameWithoutExtension(assemblyName)
                                : assemblyName;

                if ((fixedName != null) && PowerShellAssemblies.Contains(fixedName))
                {
                    return true;
                }
            }

            return false;
        }

        internal static string GetPowerShellAssemblyStrongName(string assemblyName)
        {
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                // Remove the '.dll' if it's there...
                string fixedName = assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                ? Path.GetFileNameWithoutExtension(assemblyName)
                                : assemblyName;

                if ((fixedName != null) && PowerShellAssemblies.Contains(fixedName))
                {
                    return string.Format(CultureInfo.InvariantCulture, PowerShellAssemblyStrongNameFormat, fixedName);
                }
            }

            return assemblyName;
        }

        /// <summary>
        /// If a mutex is abandoned, in our case, it is ok to proceed.
        /// </summary>
        /// <param name="mutex">The mutex to wait on. If it is null, a new one will be created.</param>
        /// <param name="initializer">The initializer to use to recreate the mutex.</param>
        /// <returns>A working mutex. If the mutex was abandoned, a new one is created to replace it.</returns>
        internal static Mutex SafeWaitMutex(Mutex mutex, MutexInitializer initializer)
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // If the Mutex has been abandoned, then the process protecting the critical section
                // is no longer valid. We need to release to continue normal operations.
                mutex.ReleaseMutex();
                ((IDisposable)mutex).Dispose();

                // Try again, throw if it still fails
                mutex = initializer();
                mutex.WaitOne();
            }

            return mutex;
        }

        internal delegate Mutex MutexInitializer();

        internal static bool Succeeded(int hresult)
        {
            return hresult >= 0;
        }

        // Attempt to determine the existing encoding
        internal static Encoding GetEncoding(string path)
        {
            if (!File.Exists(path))
            {
                return ClrFacade.GetDefaultEncoding();
            }

            byte[] initialBytes = new byte[100];
            int bytesRead = 0;

            try
            {
                using (FileStream stream = System.IO.File.OpenRead(path))
                {
                    using (BinaryReader reader = new BinaryReader(stream))
                    {
                        bytesRead = reader.Read(initialBytes, 0, 100);
                    }
                }
            }
            catch (IOException)
            {
                return ClrFacade.GetDefaultEncoding();
            }

            // Test for four-byte preambles
            string preamble = null;
            Encoding foundEncoding = ClrFacade.GetDefaultEncoding();

            if (bytesRead > 3)
            {
                preamble = string.Join("-", initialBytes[0], initialBytes[1], initialBytes[2], initialBytes[3]);

                if (encodingMap.TryGetValue(preamble, out foundEncoding))
                {
                    return foundEncoding;
                }
            }

            // Test for three-byte preambles
            if (bytesRead > 2)
            {
                preamble = string.Join("-", initialBytes[0], initialBytes[1], initialBytes[2]);
                if (encodingMap.TryGetValue(preamble, out foundEncoding))
                {
                    return foundEncoding;
                }
            }

            // Test for two-byte preambles
            if (bytesRead > 1)
            {
                preamble = string.Join("-", initialBytes[0], initialBytes[1]);
                if (encodingMap.TryGetValue(preamble, out foundEncoding))
                {
                    return foundEncoding;
                }
            }

            // Check for binary
            string initialBytesAsAscii = System.Text.Encoding.ASCII.GetString(initialBytes, 0, bytesRead);
            if (initialBytesAsAscii.IndexOfAny(nonPrintableCharacters) >= 0)
            {
                return Encoding.Unicode;
            }

            return Encoding.ASCII;
        }

        // BigEndianUTF32 encoding is possible, but requires creation
        internal static readonly Encoding BigEndianUTF32Encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        // [System.Text.Encoding]::GetEncodings() | Where-Object { $_.GetEncoding().GetPreamble() } |
        //     Add-Member ScriptProperty Preamble { $this.GetEncoding().GetPreamble() -join "-" } -PassThru |
        //     Format-Table -Auto
        internal static readonly Dictionary<string, Encoding> encodingMap =
            new Dictionary<string, Encoding>()
            {
                { "255-254", Encoding.Unicode },
                { "254-255", Encoding.BigEndianUnicode },
                { "255-254-0-0", Encoding.UTF32 },
                { "0-0-254-255", BigEndianUTF32Encoding },
                { "239-187-191", Encoding.UTF8 },
            };

        internal static readonly char[] nonPrintableCharacters = {
            (char) 0, (char) 1, (char) 2, (char) 3, (char) 4, (char) 5, (char) 6, (char) 7, (char) 8,
            (char) 11, (char) 12, (char) 14, (char) 15, (char) 16, (char) 17, (char) 18, (char) 19, (char) 20,
            (char) 21, (char) 22, (char) 23, (char) 24, (char) 25, (char) 26, (char) 28, (char) 29, (char) 30,
            (char) 31, (char) 127, (char) 129, (char) 141, (char) 143, (char) 144, (char) 157 };

        internal static readonly UTF8Encoding utf8NoBom =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

#if !UNIX
        /// <summary>
        /// Queues a CLR worker thread with impersonation of provided Windows identity.
        /// </summary>
        /// <param name="identityToImpersonate">Windows identity to impersonate or null.</param>
        /// <param name="threadProc">Thread procedure for thread.</param>
        /// <param name="state">Optional state for thread procedure.</param>
        internal static void QueueWorkItemWithImpersonation(
            WindowsIdentity identityToImpersonate,
            WaitCallback threadProc,
            object state)
        {
            object[] args = new object[3];
            args[0] = identityToImpersonate;
            args[1] = threadProc;
            args[2] = state;
            Threading.ThreadPool.QueueUserWorkItem(WorkItemCallback, args);
        }

        private static void WorkItemCallback(object callBackArgs)
        {
            object[] args = callBackArgs as object[];
            WindowsIdentity identityToImpersonate = args[0] as WindowsIdentity;
            WaitCallback callback = args[1] as WaitCallback;
            object state = args[2];

            if (identityToImpersonate != null)
            {
                WindowsIdentity.RunImpersonated(
                    identityToImpersonate.AccessToken,
                    () => callback(state));
                return;
            }

            callback(state);
        }
#endif

        /// <summary>
        /// If the command name is fully qualified then it is split into its component parts
        /// E.g., moduleName\commandName.
        /// </summary>
        /// <param name="commandName"></param>
        /// <param name="moduleName"></param>
        /// <returns>Command name and as appropriate Module name in out parameter.</returns>
        internal static string ParseCommandName(string commandName, out string moduleName)
        {
            var names = commandName.Split(Separators.Backslash, 2);
            if (names.Length == 2)
            {
                moduleName = names[0];
                return names[1];
            }

            moduleName = null;
            return commandName;
        }

        internal static ReadOnlyCollection<T> EmptyReadOnlyCollection<T>()
        {
            return EmptyReadOnlyCollectionHolder<T>._instance;
        }

        private static class EmptyReadOnlyCollectionHolder<T>
        {
            internal static readonly ReadOnlyCollection<T> _instance =
                new ReadOnlyCollection<T>(Array.Empty<T>());
        }

        internal static class Separators
        {
            internal static readonly char[] Backslash = new char[] { '\\' };
            internal static readonly char[] Directory = new char[] { '\\', '/' };
            internal static readonly char[] DirectoryOrDrive = new char[] { '\\', '/', ':' };

            internal static readonly char[] Colon = new char[] { ':' };
            internal static readonly char[] Dot = new char[] { '.' };
            internal static readonly char[] Pipe = new char[] { '|' };
            internal static readonly char[] Comma = new char[] { ',' };
            internal static readonly char[] Semicolon = new char[] { ';' };
            internal static readonly char[] StarOrQuestion = new char[] { '*', '?' };
            internal static readonly char[] ColonOrBackslash = new char[] { '\\', ':' };
            internal static readonly char[] PathSeparator = new char[] { Path.PathSeparator };

            internal static readonly char[] QuoteChars = new char[] { '\'', '"' };
            internal static readonly char[] Space = new char[] { ' ' };
            internal static readonly char[] QuotesSpaceOrTab = new char[] { ' ', '\t', '\'', '"' };
            internal static readonly char[] SpaceOrTab = new char[] { ' ', '\t' };
            internal static readonly char[] Newline = new char[] { '\n' };
            internal static readonly char[] CrLf = new char[] { '\r', '\n' };

            // (Copied from System.IO.Path so we can call TrimEnd in the same way that Directory.EnumerateFiles would on the search patterns).
            // Trim trailing white spaces, tabs etc but don't be aggressive in removing everything that has UnicodeCategory of trailing space.
            // String.WhitespaceChars will trim aggressively than what the underlying FS does (for ex, NTFS, FAT).
            internal static readonly char[] PathSearchTrimEnd = { (char)0x9, (char)0xA, (char)0xB, (char)0xC, (char)0xD, (char)0x20, (char)0x85, (char)0xA0 };
        }

        /// <summary>
        /// A COM object could be directly of the type 'System.__ComObject', or it could be a strongly typed RWC,
        /// whose specific type derives from 'System.__ComObject'.
        /// A strongly typed RWC can be created via the 'new' operation with a Primary Interop Assembly (PIA).
        /// For example, with the PIA 'Microsoft.Office.Interop.Excel', you can write the following code:
        ///    var excelApp = new Microsoft.Office.Interop.Excel.Application();
        ///    Type type = excelApp.GetType();
        ///    Type comObjectType = typeof(object).Assembly.GetType("System.__ComObject");
        ///    Console.WriteLine("excelApp type: {0}", type.FullName);
        ///    Console.WriteLine("Is __ComObject assignable from? {0}", comObjectType.IsAssignableFrom(type));
        /// and the results are:
        ///    excelApp type: Microsoft.Office.Interop.Excel.ApplicationClass
        ///    Is __ComObject assignable from? True.
        /// </summary>
        internal static bool IsComObject(object obj)
        {
#if UNIX
            return false;
#else
            return obj != null && Marshal.IsComObject(obj);
#endif
        }

        /// <summary>
        /// EnforceSystemLockDownLanguageMode
        ///     FullLangauge        ->  ConstrainedLanguage
        ///     RestrictedLanguage  ->  NoLanguage
        ///     ConstrainedLanguage ->  ConstrainedLanguage
        ///     NoLanguage          ->  NoLanguage.
        /// </summary>
        /// <param name="context">ExecutionContext.</param>
        /// <returns>Previous language mode or null for no language mode change.</returns>
        internal static PSLanguageMode? EnforceSystemLockDownLanguageMode(ExecutionContext context)
        {
            PSLanguageMode? oldMode = null;

            if (SystemPolicy.GetSystemLockdownPolicy() == SystemEnforcementMode.Enforce)
            {
                switch (context.LanguageMode)
                {
                    case PSLanguageMode.FullLanguage:
                        oldMode = context.LanguageMode;
                        context.LanguageMode = PSLanguageMode.ConstrainedLanguage;
                        break;

                    case PSLanguageMode.RestrictedLanguage:
                        oldMode = context.LanguageMode;
                        context.LanguageMode = PSLanguageMode.NoLanguage;
                        break;

                    case PSLanguageMode.ConstrainedLanguage:
                    case PSLanguageMode.NoLanguage:
                        break;

                    default:
                        Diagnostics.Assert(false, "Unexpected PSLanguageMode");
                        oldMode = context.LanguageMode;
                        context.LanguageMode = PSLanguageMode.NoLanguage;
                        break;
                }
            }

            return oldMode;
        }

        #region Implicit Remoting Batching

        // Commands allowed to run on target remote session along with implicit remote commands
        private static readonly HashSet<string> AllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ForEach-Object",
            "Measure-Command",
            "Measure-Object",
            "Sort-Object",
            "Where-Object"
        };

        // Determines if the typed command invokes implicit remoting module proxy functions in such
        // a way as to allow simple batching, to reduce round trips between client and server sessions.
        // Requirements:
        //  a. All commands must be implicit remoting module proxy commands targeted to the same remote session
        //  b. Except for *allowed* commands that can be safely run on remote session rather than client session
        //  c. Commands must be in a simple pipeline
        internal static bool TryRunAsImplicitBatch(string command, Runspace runspace)
        {
            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                ps.Runspace = runspace;

                try
                {
                    var scriptBlock = ScriptBlock.Create(command);
                    var scriptBlockAst = scriptBlock.Ast as ScriptBlockAst;
                    if (scriptBlockAst == null)
                    {
                        return false;
                    }

                    // Make sure that this is a simple pipeline
                    string errorId;
                    string errorMsg;
                    scriptBlockAst.GetSimplePipeline(true, out errorId, out errorMsg);
                    if (errorId != null)
                    {
                        WriteVerbose(ps, ParserStrings.ImplicitRemotingPipelineBatchingNotASimplePipeline);
                        return false;
                    }

                    // Run checker
                    var checker = new PipelineForBatchingChecker { ScriptBeingConverted = scriptBlockAst };
                    scriptBlockAst.InternalVisit(checker);

                    // If this is just a single command, there is no point in batching it
                    if (checker.Commands.Count < 2)
                    {
                        return false;
                    }

                    // We have a valid batching candidate

                    // Check commands
                    if (!TryGetCommandInfoList(ps, checker.Commands, out Collection<CommandInfo> cmdInfoList))
                    {
                        return false;
                    }

                    // All command modules must be implicit remoting modules from the same PSSession
                    var success = true;
                    var psSessionId = Guid.Empty;
                    foreach (var cmdInfo in cmdInfoList)
                    {
                        // Check for allowed command
                        string cmdName = (cmdInfo is AliasInfo aliasInfo) ? aliasInfo.ReferencedCommand.Name : cmdInfo.Name;
                        if (AllowedCommands.Contains(cmdName))
                        {
                            continue;
                        }

                        // Commands must be from implicit remoting module
                        if (cmdInfo.Module == null || string.IsNullOrEmpty(cmdInfo.ModuleName))
                        {
                            WriteVerbose(ps, string.Format(CultureInfo.CurrentCulture, ParserStrings.ImplicitRemotingPipelineBatchingNotImplicitCommand, cmdInfo.Name));
                            success = false;
                            break;
                        }

                        // Commands must be from modules imported into the same remote session
                        if (cmdInfo.Module.PrivateData is System.Collections.Hashtable privateData)
                        {
                            var sessionIdString = privateData["ImplicitSessionId"] as string;
                            if (string.IsNullOrEmpty(sessionIdString))
                            {
                                WriteVerbose(ps, string.Format(CultureInfo.CurrentCulture, ParserStrings.ImplicitRemotingPipelineBatchingNotImplicitCommand, cmdInfo.Name));
                                success = false;
                                break;
                            }

                            var sessionId = new Guid(sessionIdString);
                            if (psSessionId == Guid.Empty)
                            {
                                psSessionId = sessionId;
                            }
                            else if (psSessionId != sessionId)
                            {
                                WriteVerbose(ps, string.Format(CultureInfo.CurrentCulture, ParserStrings.ImplicitRemotingPipelineBatchingWrongSession, cmdInfo.Name));
                                success = false;
                                break;
                            }
                        }
                        else
                        {
                            WriteVerbose(ps, string.Format(CultureInfo.CurrentCulture, ParserStrings.ImplicitRemotingPipelineBatchingNotImplicitCommand, cmdInfo.Name));
                            success = false;
                            break;
                        }
                    }

                    if (success)
                    {
                        //
                        // Invoke command pipeline as entire pipeline on remote session
                        //

                        // Update script to declare variables via Using keyword
                        if (checker.ValidVariables.Count > 0)
                        {
                            foreach (var variableName in checker.ValidVariables)
                            {
                                command = command.Replace(variableName, ("Using:" + variableName), StringComparison.OrdinalIgnoreCase);
                            }

                            scriptBlock = ScriptBlock.Create(command);
                        }

                        // Retrieve the PSSession runspace in which to run the batch script on
                        ps.Commands.Clear();
                        ps.Commands.AddCommand("Get-PSSession").AddParameter("InstanceId", psSessionId);
                        var psSession = ps.Invoke<System.Management.Automation.Runspaces.PSSession>().FirstOrDefault();
                        if (psSession == null || (ps.Streams.Error.Count > 0) || (psSession.Availability != RunspaceAvailability.Available))
                        {
                            WriteVerbose(ps, ParserStrings.ImplicitRemotingPipelineBatchingNoPSSession);
                            return false;
                        }

                        WriteVerbose(ps, ParserStrings.ImplicitRemotingPipelineBatchingSuccess);

                        // Create and invoke implicit remoting command pipeline
                        ps.Commands.Clear();
                        ps.AddCommand("Invoke-Command").AddParameter("Session", psSession).AddParameter("ScriptBlock", scriptBlock).AddParameter("HideComputerName", true)
                            .AddCommand("Out-Default");
                        foreach (var cmd in ps.Commands.Commands)
                        {
                            cmd.MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
                        }

                        try
                        {
                            ps.Invoke();
                        }
                        catch (Exception ex)
                        {
                            var errorRecord = new ErrorRecord(ex, "ImplicitRemotingBatchExecutionTerminatingError", ErrorCategory.InvalidOperation, null);

                            ps.Commands.Clear();
                            ps.AddCommand("Write-Error").AddParameter("InputObject", errorRecord).Invoke();
                        }

                        return true;
                    }
                }
                catch (ImplicitRemotingBatchingNotSupportedException ex)
                {
                    WriteVerbose(ps, string.Format(CultureInfo.CurrentCulture, "{0} : {1}", ex.Message, ex.ErrorId));
                }
                catch (Exception ex)
                {
                    WriteVerbose(ps, string.Format(CultureInfo.CurrentCulture, ParserStrings.ImplicitRemotingPipelineBatchingException, ex.Message));
                }
            }

            return false;
        }

        private static void WriteVerbose(PowerShell ps, string msg)
        {
            ps.Commands.Clear();
            ps.AddCommand("Write-Verbose").AddParameter("Message", msg).Invoke();
        }

        private const string WhereObjectCommandAlias = "?";

        private static bool TryGetCommandInfoList(PowerShell ps, HashSet<string> commandNames, out Collection<CommandInfo> cmdInfoList)
        {
            if (commandNames.Count == 0)
            {
                cmdInfoList = null;
                return false;
            }

            bool specialCaseWhereCommandAlias = commandNames.Contains(WhereObjectCommandAlias);
            if (specialCaseWhereCommandAlias)
            {
                commandNames.Remove(WhereObjectCommandAlias);
            }

            // Use Get-Command to collect CommandInfo from candidate commands, with correct precedence so
            // that implicit remoting proxy commands will appear when available.
            ps.Commands.Clear();
            ps.Commands.AddCommand("Get-Command").AddParameter("Name", commandNames.ToArray());
            cmdInfoList = ps.Invoke<CommandInfo>();
            if (ps.Streams.Error.Count > 0)
            {
                return false;
            }

            // For special case '?' alias don't use Get-Command to retrieve command info, and instead
            // use the GetCommand API.
            if (specialCaseWhereCommandAlias)
            {
                var cmdInfo = ps.Runspace.ExecutionContext.SessionState.InvokeCommand.GetCommand(WhereObjectCommandAlias, CommandTypes.Alias);
                if (cmdInfo == null)
                {
                    return false;
                }

                cmdInfoList.Add(cmdInfo);
            }

            return true;
        }

        #endregion
    }

    #region ImplicitRemotingBatching

    // A visitor to walk an AST and validate that it is a candidate for implicit remoting batching.
    // Based on ScriptBlockToPowerShellChecker.
    internal class PipelineForBatchingChecker : AstVisitor
    {
        internal readonly HashSet<string> ValidVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal ScriptBlockAst ScriptBeingConverted { get; set; }

        public override AstVisitAction VisitVariableExpression(VariableExpressionAst variableExpressionAst)
        {
            if (!variableExpressionAst.VariablePath.IsAnyLocal())
            {
                ThrowError(
                        new ImplicitRemotingBatchingNotSupportedException(
                            "VariableTypeNotSupported"),
                        variableExpressionAst);
            }

            if (variableExpressionAst.VariablePath.UnqualifiedPath != "_")
            {
                ValidVariables.Add(variableExpressionAst.VariablePath.UnqualifiedPath);
            }

            return AstVisitAction.Continue;
        }

        public override AstVisitAction VisitPipeline(PipelineAst pipelineAst)
        {
            if (pipelineAst.PipelineElements[0] is CommandExpressionAst)
            {
                // If the first element is a CommandExpression, this pipeline should be the value
                // of a parameter. We want to avoid a scriptblock that contains only a pure expression.
                // The check "pipelineAst.Parent.Parent == ScriptBeingConverted" guarantees we throw
                // error on that kind of scriptblock.

                // Disallow pure expressions at the "top" level, but allow them otherwise.
                // We want to catch:
                //     1 | echo
                // But we don't want to error out on:
                //     echo $(1)
                // See the comment in VisitCommand on why it's safe to check Parent.Parent, we
                // know that we have at least:
                //     * a NamedBlockAst (the end block)
                //     * a ScriptBlockAst (the ast we're comparing to)
                if (pipelineAst.GetPureExpression() == null || pipelineAst.Parent.Parent == ScriptBeingConverted)
                {
                    ThrowError(
                        new ImplicitRemotingBatchingNotSupportedException(
                            "PipelineStartingWithExpressionNotSupported"),
                        pipelineAst);
                }
            }

            return AstVisitAction.Continue;
        }

        public override AstVisitAction VisitCommand(CommandAst commandAst)
        {
            if (commandAst.InvocationOperator == TokenKind.Dot)
            {
                ThrowError(
                    new ImplicitRemotingBatchingNotSupportedException(
                        "DotSourcingNotSupported"),
                    commandAst);
            }

            /*
            // Up front checking ensures that we have a simple script block,
            // so we can safely assume that the parents are:
            //     * a PipelineAst
            //     * a NamedBlockAst (the end block)
            //     * a ScriptBlockAst (the ast we're comparing to)
            // If that isn't the case, the conversion isn't allowed.  It
            // is also safe to assume that we have at least 3 parents, a script block can't be simpler.
            if (commandAst.Parent.Parent.Parent != ScriptBeingConverted)
            {
                ThrowError(
                    new ImplicitRemotingBatchingNotSupportedException(
                        "CantConvertWithCommandInvocations not supported"),
                    commandAst);
            }
            */

            if (commandAst.CommandElements[0] is ScriptBlockExpressionAst)
            {
                ThrowError(
                    new ImplicitRemotingBatchingNotSupportedException(
                        "ScriptBlockInvocationNotSupported"),
                    commandAst);
            }

            var commandName = commandAst.GetCommandName();
            if (commandName != null)
            {
                Commands.Add(commandName);
            }

            return AstVisitAction.Continue;
        }

        public override AstVisitAction VisitMergingRedirection(MergingRedirectionAst redirectionAst)
        {
            if (redirectionAst.ToStream != RedirectionStream.Output)
            {
                ThrowError(
                    new ImplicitRemotingBatchingNotSupportedException(
                        "MergeRedirectionNotSupported"),
                    redirectionAst);
            }

            return AstVisitAction.Continue;
        }

        public override AstVisitAction VisitFileRedirection(FileRedirectionAst redirectionAst)
        {
            ThrowError(
                new ImplicitRemotingBatchingNotSupportedException(
                    "FileRedirectionNotSupported"),
                redirectionAst);

            return AstVisitAction.Continue;
        }

        /*
        public override AstVisitAction VisitScriptBlockExpression(ScriptBlockExpressionAst scriptBlockExpressionAst)
        {
            ThrowError(new ImplicitRemotingBatchingNotSupportedException(
                           "ScriptBlocks not supported"),
                       scriptBlockExpressionAst);

            return AstVisitAction.SkipChildren;
        }
        */

        public override AstVisitAction VisitUsingExpression(UsingExpressionAst usingExpressionAst)
        {
            // Using expressions are not expected in Implicit remoting commands.
            ThrowError(new ImplicitRemotingBatchingNotSupportedException(
                "UsingExpressionNotSupported"),
                usingExpressionAst);

            return AstVisitAction.SkipChildren;
        }

        internal static void ThrowError(ImplicitRemotingBatchingNotSupportedException ex, Ast ast)
        {
            InterpreterError.UpdateExceptionErrorRecordPosition(ex, ast.Extent);
            throw ex;
        }
    }

    internal class ImplicitRemotingBatchingNotSupportedException : Exception
    {
        internal string ErrorId
        {
            get;
            private set;
        }

        internal ImplicitRemotingBatchingNotSupportedException(string errorId) : base(
            ParserStrings.ImplicitRemotingPipelineBatchingNotSupported)
        {
            ErrorId = errorId;
        }
    }

    #endregion
}

namespace System.Management.Automation.Internal
{
    /// <summary>This class is used for internal test purposes.</summary>
    [SuppressMessage("Microsoft.MSInternal", "CA903:InternalNamespaceShouldNotContainPublicTypes", Justification = "Needed Internal use only")]
    public static class InternalTestHooks
    {
        internal static bool BypassGroupPolicyCaching;
        internal static bool ForceScriptBlockLogging;
        internal static bool UseDebugAmsiImplementation;
        internal static bool BypassAppLockerPolicyCaching;
        internal static bool BypassOnlineHelpRetrieval;
        internal static bool ForcePromptForChoiceDefaultOption;

        // Stop/Restart/Rename Computer tests
        internal static bool TestStopComputer;
        internal static bool TestWaitStopComputer;
        internal static bool TestRenameComputer;
        internal static int TestStopComputerResults;
        internal static int TestRenameComputerResults;

        // It's useful to test that we don't depend on the ScriptBlock and AST objects and can use a re-parsed version.
        internal static bool IgnoreScriptBlockCache;
        // Simulate 'System.Diagnostics.Stopwatch.IsHighResolution is false' to test Get-Uptime throw
        internal static bool StopwatchIsNotHighResolution;
        internal static bool DisableGACLoading;
        internal static bool SetConsoleWidthToZero;
        internal static bool SetConsoleHeightToZero;

        // A location to test PSEdition compatibility functionality for Windows PowerShell modules with
        // since we can't manipulate the System32 directory in a test
        internal static string TestWindowsPowerShellPSHomeLocation;

        // A version of Windows PS that is installed on the system; normally this is retrieved from a reg key that is write-protected.
        internal static string TestWindowsPowerShellVersionString;

        internal static bool ShowMarkdownOutputBypass;

        /// <summary>This member is used for internal test purposes.</summary>
        public static void SetTestHook(string property, object value)
        {
            var fieldInfo = typeof(InternalTestHooks).GetField(property, BindingFlags.Static | BindingFlags.NonPublic);
            if (fieldInfo != null)
            {
                fieldInfo.SetValue(null, value);
            }
        }

        /// <summary>
        /// Test hook used to test implicit remoting batching.  A local runspace must be provided that has imported a
        /// remote session, i.e., has run the Import-PSSession cmdlet.  This hook will return true if the provided commandPipeline
        /// is successfully batched and run in the remote session, and false if it is rejected for batching.
        /// </summary>
        /// <param name="commandPipeline">Command pipeline to test.</param>
        /// <param name="runspace">Runspace with imported remote session.</param>
        /// <returns>True if commandPipeline is batched successfully.</returns>
        public static bool TestImplicitRemotingBatching(string commandPipeline, System.Management.Automation.Runspaces.Runspace runspace)
        {
            return Utils.TryRunAsImplicitBatch(commandPipeline, runspace);
        }

        /// <summary>
        /// Constructs a custom PSSenderInfo instance that can be assigned to $PSSenderInfo
        /// in order to simulate a remoting session with respect to the $PSSenderInfo.ConnectionString (connection URL)
        /// and $PSSenderInfo.ApplicationArguments.PSVersionTable.PSVersion (the remoting client's PowerShell version).
        /// See Get-FormatDataTest.ps1.
        /// </summary>
        /// <param name="url">The connection URL to reflect in the returned instance's ConnectionString property.</param>
        /// <param name="clientVersion">The version number to report as the remoting client's PowerShell version.</param>
        /// <returns>The newly constructed custom PSSenderInfo instance.</returns>
        public static PSSenderInfo GetCustomPSSenderInfo(string url, Version clientVersion)
        {
            var dummyPrincipal = new PSPrincipal(new PSIdentity("none", true, "someuser", null), null);
            var pssi = new PSSenderInfo(dummyPrincipal, url);
            pssi.ApplicationArguments = new PSPrimitiveDictionary();
            pssi.ApplicationArguments.Add("PSVersionTable", new PSObject(new PSPrimitiveDictionary()));
            ((PSPrimitiveDictionary)PSObject.Base(pssi.ApplicationArguments["PSVersionTable"])).Add("PSVersion", new PSObject(clientVersion));
            return pssi;
        }
    }

    /// <summary>
    /// Provides undo/redo functionality by using 2 instances of <seealso cref="BoundedStack{T}"/>.
    /// </summary>
    internal class HistoryStack<T>
    {
        private readonly BoundedStack<T> _boundedUndoStack;
        private readonly BoundedStack<T> _boundedRedoStack;

        internal HistoryStack(uint capacity)
        {
            _boundedUndoStack = new BoundedStack<T>(capacity);
            _boundedRedoStack = new BoundedStack<T>(capacity);
        }

        internal void Push(T item)
        {
            _boundedUndoStack.Push(item);
            if (RedoCount >= 0)
            {
                _boundedRedoStack.Clear();
            }
        }

        /// <summary>
        /// Handles bounded history stacks by pushing the current item to the redoStack and returning the item from the popped undoStack.
        /// </summary>
        internal T Undo(T currentItem)
        {
            T previousItem = _boundedUndoStack.Pop();
            _boundedRedoStack.Push(currentItem);
            return previousItem;
        }

        /// <summary>
        /// Handles bounded history stacks by pushing the current item to the undoStack and returning the item from the popped redoStack.
        /// </summary>
        internal T Redo(T currentItem)
        {
            var nextItem = _boundedRedoStack.Pop();
            _boundedUndoStack.Push(currentItem);
            return nextItem;
        }

        internal int UndoCount => _boundedUndoStack.Count;

        internal int RedoCount => _boundedRedoStack.Count;
    }

    /// <summary>
    /// A bounded stack based on a linked list.
    /// </summary>
    internal class BoundedStack<T> : LinkedList<T>
    {
        private readonly uint _capacity;

        /// <summary>
        /// Lazy initialisation, i.e. it sets only its limit but does not allocate the memory for the given capacity.
        /// </summary>
        /// <param name="capacity"></param>
        internal BoundedStack(uint capacity)
        {
            _capacity = capacity;
        }

        /// <summary>
        /// Push item.
        /// </summary>
        /// <param name="item"></param>
        internal void Push(T item)
        {
            this.AddFirst(item);

            if (this.Count > _capacity)
            {
                this.RemoveLast();
            }
        }

        /// <summary>
        /// Pop item.
        /// </summary>
        /// <returns></returns>
        internal T Pop()
        {
            if (this.First == null)
            {
                throw new InvalidOperationException(SessionStateStrings.BoundedStackIsEmpty);
            }

            var item = this.First.Value;
            try
            {
                this.RemoveFirst();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException(SessionStateStrings.BoundedStackIsEmpty);
            }

            return item;
        }
    }

    /// <summary>
    /// A readonly Hashset.
    /// </summary>
    internal sealed class ReadOnlyBag<T> : IEnumerable
    {
        private HashSet<T> _hashset;

        /// <summary>
        /// Constructor for the readonly Hashset.
        /// </summary>
        internal ReadOnlyBag(HashSet<T> hashset)
        {
            if (hashset == null)
            {
                throw new ArgumentNullException(nameof(hashset));
            }

            _hashset = hashset;
        }

        /// <summary>
        /// Get the count of the Hashset.
        /// </summary>
        public int Count => _hashset.Count;

        /// <summary>
        /// Indicate if it's a readonly Hashset.
        /// </summary>
        public bool IsReadOnly => true;

        /// <summary>
        /// Check if the set contains an item.
        /// </summary>
        public bool Contains(T item) => _hashset.Contains(item);

        /// <summary>
        /// GetEnumerator method.
        /// </summary>
        public IEnumerator GetEnumerator() => _hashset.GetEnumerator();

        /// <summary>
        /// Get an empty singleton.
        /// </summary>
        internal static readonly ReadOnlyBag<T> Empty = new ReadOnlyBag<T>(new HashSet<T>(capacity: 0));
    }
}
