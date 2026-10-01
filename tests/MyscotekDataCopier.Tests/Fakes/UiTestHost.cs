using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace MyscotekDataCopier.Tests.Fakes
{
    /// <summary>
    /// Hosts WinForms code for the UI tests: each test body runs on its own STA thread (WinForms needs
    /// one) with XrmToolBox's toast-notification assembly replaced by an in-memory stub (see
    /// <see cref="ToastNotificationsStub"/>), so constructing the real plugin control has no side
    /// effects outside the test run.
    /// </summary>
    public static class UiTestHost
    {
        /// <summary>Longest a UI test may run; a modal dialog nobody closes would otherwise hang the run.</summary>
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Runs <paramref name="body"/> on a new STA thread, inside a message loop, and rethrows whatever
        /// it throws. Like XrmToolBox's UI thread the body runs within Application.Run, so the thread
        /// keeps its WindowsFormsSynchronizationContext: awaited continuations come back to it (they run
        /// while <see cref="PumpUntil"/> pumps). A top-level Application.DoEvents would instead end a
        /// message loop, and WinForms uninstalls the context when the outermost loop ends. Touching a
        /// control from another thread throws.
        /// </summary>
        public static void Run(Action body)
        {
            ToastNotificationsStub.Install();
            Control.CheckForIllegalCrossThreadCalls = true;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    SynchronizationContext.Current.Post(_ =>
                    {
                        try
                        {
                            body();
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                        finally
                        {
                            Application.ExitThread();
                        }
                    }, null);
                    Application.Run();
                }
                catch (Exception ex)
                {
                    failure = failure ?? ex;
                }
            })
            {
                IsBackground = true,
                Name = "UI test"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TestTimeout))
                throw new TimeoutException($"The UI test did not finish within {TestTimeout.TotalSeconds:0} seconds (a modal dialog?).");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>
        /// Pumps the message loop (so awaited continuations and BeginInvoke calls run) until
        /// <paramref name="condition"/> holds.
        /// </summary>
        public static void PumpUntil(Func<bool> condition, string waitingFor, int timeoutMilliseconds = 15000)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeoutMilliseconds)
                    throw new TimeoutException("Timed out waiting for " + waitingFor + ".");
                Application.DoEvents();
                Thread.Sleep(5);
            }
            Application.DoEvents();
        }

        /// <summary>Pumps the message loop for about <paramref name="milliseconds"/>.</summary>
        public static void Pump(int milliseconds = 50)
        {
            Stopwatch watch = Stopwatch.StartNew();
            do
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
            while (watch.ElapsedMilliseconds < milliseconds);
        }

        /// <summary>The control called <paramref name="name"/> anywhere under <paramref name="root"/>.</summary>
        public static T Find<T>(Control root, string name) where T : Control
        {
            Control[] found = root.Controls.Find(name, searchAllChildren: true);
            if (found.Length != 1) throw new InvalidOperationException($"Expected exactly one control named '{name}', found {found.Length}.");
            return (T)found[0];
        }

        /// <summary>The toolbar item called <paramref name="name"/> on the first ToolStrip under <paramref name="root"/>.</summary>
        public static T FindToolItem<T>(Control root, string name) where T : ToolStripItem
        {
            ToolStrip strip = Descendants(root).OfType<ToolStrip>().First();
            return (T)(strip.Items[name] ?? throw new InvalidOperationException($"No toolbar item named '{name}'."));
        }

        public static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;
                foreach (Control grandChild in Descendants(child)) yield return grandChild;
            }
        }
    }

    /// <summary>
    /// XrmToolBox's <c>PluginControlBase</c> constructor subscribes to
    /// <c>Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated</c>. In any
    /// process other than XrmToolBox that subscription registers the process for toast notifications
    /// in HKCU (a COM LocalServer32 key pointing at the test host and an AppUserModelId key) and saves
    /// an icon under %LocalAppData%. The test project therefore never copies that assembly (see the
    /// csproj), and this resolver answers the reference with an in-memory assembly that has the three
    /// types the constructor touches and does nothing.
    /// </summary>
    internal static class ToastNotificationsStub
    {
        internal const string AssemblyShortName = "Microsoft.Toolkit.Uwp.Notifications";

        /// <summary>
        /// The toolkit's public key (token 4aff67a105548ee2), read from the package's metadata. The
        /// loader only binds the strong-named reference to an assembly with this identity; nothing is
        /// signed or verified here - the stub is an in-memory test double.
        /// </summary>
        private const string ToolkitPublicKey =
            "002400000480000094000000060200000024000052534131000400000100010041753af735ae6140c9508567666c51c6" +
            "ab929806adb0d210694b30ab142a060237bc741f9682e7d8d4310364b4bba4ee89cc9d3d5ce7e5583587e8ea44dca099" +
            "77996582875e71fb54fa7b170798d853d5d8010b07219633bdb761d01ac924da44576d6180cdceae537973982bb461c5" +
            "41541d58417a3794e34f45e6f2d129e2";

        private static readonly object Sync = new object();
        private static Assembly _stub;
        private static bool _installed;

        public static void Install()
        {
            lock (Sync)
            {
                if (_installed) return;
                string real = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AssemblyShortName + ".dll");
                if (File.Exists(real))
                {
                    throw new InvalidOperationException(
                        $"{real} must not be deployed next to the tests: constructing an XrmToolBox control would register the test host for toast notifications in the registry. Keep XrmToolBoxPackage runtime assets excluded in the test csproj.");
                }
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                _installed = true;
            }
        }

        /// <summary>True when the toolkit reference was answered by the stub (never by the real assembly).</summary>
        public static bool IsStubLoaded =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.IsDynamic && a.GetName().Name == AssemblyShortName);

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            if (!string.Equals(new AssemblyName(args.Name).Name, AssemblyShortName, StringComparison.OrdinalIgnoreCase)) return null;
            lock (Sync)
            {
                return _stub ?? (_stub = Build());
            }
        }

        private static Assembly Build()
        {
            const string ns = AssemblyShortName + ".";
            var name = new AssemblyName(AssemblyShortName) { Version = new Version(7, 1, 0, 0) };
            name.SetPublicKey(HexToBytes(ToolkitPublicKey));
            AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
            ModuleBuilder module = assembly.DefineDynamicModule(AssemblyShortName);

            TypeBuilder eventArgs = module.DefineType(ns + "ToastNotificationActivatedEventArgsCompat",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            eventArgs.DefineDefaultConstructor(MethodAttributes.Public);
            Type eventArgsType = eventArgs.CreateType();

            // public delegate void OnActivated(ToastNotificationActivatedEventArgsCompat e);
            TypeBuilder handler = module.DefineType(ns + "OnActivated",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.AutoClass, typeof(MulticastDelegate));
            ConstructorBuilder constructor = handler.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard, new[] { typeof(object), typeof(IntPtr) });
            constructor.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            MethodBuilder invoke = handler.DefineMethod("Invoke",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                typeof(void), new[] { eventArgsType });
            invoke.SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
            Type handlerType = handler.CreateType();

            // public static class ToastNotificationManagerCompat { public static event OnActivated OnActivated; } - accessors do nothing
            TypeBuilder manager = module.DefineType(ns + "ToastNotificationManagerCompat",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
            foreach (string accessor in new[] { "add_OnActivated", "remove_OnActivated" })
            {
                MethodBuilder method = manager.DefineMethod(accessor,
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                    typeof(void), new[] { handlerType });
                method.GetILGenerator().Emit(OpCodes.Ret);
            }
            manager.CreateType();
            return assembly;
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}
