// Copyright 2013 Xamarin Inc. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using MonoTouch.Tuner;

using Mono.Cecil;
using Mono.Tuner;
using Xamarin.Linker;

using Xamarin.Utils;
using Xamarin.MacDev;

namespace Xamarin.Bundler {

	public enum BitCodeMode {
		None = 0,
		ASMOnly = 1,
		LLVMOnly = 2,
		MarkerOnly = 3,
	}

	[Flags]
	public enum Abi {
		None   =   0,
		i386   =   1,
		ARMv6  =   2,
		ARMv7  =   4,
		ARMv7s =   8,
		ARM64 =   16,
		x86_64 =  32,
		Thumb  =  64,
		LLVM   = 128,
		ARMv7k = 256,
		SimulatorArchMask = i386 | x86_64,
		DeviceArchMask = ARMv6 | ARMv7 | ARMv7s | ARMv7k | ARM64,
		ArchMask = SimulatorArchMask | DeviceArchMask,
		Arch64Mask = x86_64 | ARM64,
		Arch32Mask = i386 | ARMv6 | ARMv7 | ARMv7s | ARMv7k,
	}

	public static class AbiExtensions {
		public static string AsString (this Abi self)
		{
			var rv = (self & Abi.ArchMask).ToString ();
			if ((self & Abi.LLVM) == Abi.LLVM)
				rv += "+LLVM";
			if ((self & Abi.Thumb) == Abi.Thumb)
				rv += "+Thumb";
			return rv;
		}

		public static string AsArchString (this Abi self)
		{
			return (self & Abi.ArchMask).ToString ().ToLowerInvariant ();
		}
	}

	public enum RegistrarMode {
		Default,
		Dynamic,
		Static,
	}

	public enum BuildTarget {
		Simulator,
		Device,
	}

	public enum DlsymOptions
	{
		Default,
		All,
		None,
		Custom,
	}

	public partial class Application
	{
		public string ExecutableName;
		public BuildTarget BuildTarget;

		public bool EnableCxx;
		public bool EnableProfiling;
		bool? package_mdb;
		public bool PackageMdb {
			get { return package_mdb.Value; }
			set { package_mdb = value; }
		}
		bool? enable_msym;
		public bool EnableMSym {
			get { return enable_msym.Value; }
			set { enable_msym = value; }
		}
		public bool EnableRepl;

		public bool IsExtension;
		public List<string> Extensions = new List<string> (); // A list of the extensions this app contains.
		public List<Application> AppExtensions = new List<Application> ();

		public bool FastDev;

		public bool? EnablePie;
		public bool NativeStrip = true;
		public string SymbolList;
		public bool ManagedStrip = true;
		public List<string> NoSymbolStrip = new List<string> ();
		
		public bool? ThreadCheck;
		public DlsymOptions DlsymOptions;
		public List<Tuple<string, bool>> DlsymAssemblies;
		public bool? UseMonoFramework;
		public bool? PackageMonoFramework;

		public bool NoFastSim;

		// The list of assemblies that we do generate debugging info for.
		public bool DebugAll;
		public List<string> DebugAssemblies = new List<string> ();

		public bool? DebugTrack;

		public string Compiler = string.Empty;
		public string CompilerPath;

		public string AotArguments = "static,asmonly,direct-icalls,";
		public string AotOtherArguments = string.Empty;
		public bool? LLVMAsmWriter;

		public Dictionary<string, string> EnvironmentVariables = new Dictionary<string, string> ();

		//
		// Linker config
		//

		public bool LinkAway = true;
		public bool LinkerDumpDependencies { get; set; }
		public List<string> References = new List<string> ();
		
		public bool? BuildDSym;
		public bool Is32Build { get { return IsArchEnabled (Abi.Arch32Mask); } } // If we're targetting a 32 bit arch.
		public bool Is64Build { get { return IsArchEnabled (Abi.Arch64Mask); } } // If we're targetting a 64 bit arch.
		public bool IsDualBuild { get { return Is32Build && Is64Build; } } // if we're building both a 32 and a 64 bit version.
		public bool IsLLVM { get { return IsArchEnabled (Abi.LLVM); } }

		public List<Target> Targets = new List<Target> ();

		public string UserGccFlags;

		// If we didn't link the final executable because the existing binary is up-to-date.
		bool cached_executable; 

		List<Abi> abis;
		HashSet<Abi> all_architectures; // all Abis used in the app, including extensions.

		BuildTasks build_tasks;

		Dictionary<string, Tuple<AssemblyBuildTarget, string>> assembly_build_targets = new Dictionary<string, Tuple<AssemblyBuildTarget, string>> ();

		public AssemblyBuildTarget LibMonoLinkMode = AssemblyBuildTarget.StaticObject;
		public AssemblyBuildTarget LibXamarinLinkMode = AssemblyBuildTarget.StaticObject;
		public AssemblyBuildTarget LibPInvokesLinkMode => LibXamarinLinkMode;
		public AssemblyBuildTarget LibRegistrarLinkMode => LibXamarinLinkMode;

		public bool OnlyStaticLibraries {
			get {
				return assembly_build_targets.All ((abt) => abt.Value.Item1 == AssemblyBuildTarget.StaticObject);
			}
		}

		public bool HasDynamicLibraries {
			get {
				return assembly_build_targets.Any ((abt) => abt.Value.Item1 == AssemblyBuildTarget.DynamicLibrary);
			}
		}

		public bool HasFrameworks {
			get {
				return assembly_build_targets.Any ((abt) => abt.Value.Item1 == AssemblyBuildTarget.Framework);
			}
		}

		public void AddAssemblyBuildTarget (string value)
		{
			var eq_index = value.IndexOf ('=');
			if (eq_index == -1)
				throw ErrorHelper.CreateError (10, "Could not parse the command line arguments: --assembly-build-target={0}", value);

			var assembly_name = value.Substring (0, eq_index);
			string target, name;

			var eq_index2 = value.IndexOf ('=', eq_index + 1);
			if (eq_index2 == -1) {
				target = value.Substring (eq_index + 1);
				if (assembly_name == "@all" || assembly_name == "@sdk") {
					name = string.Empty;
				} else {
					name = assembly_name;
				}
			} else {
				target = value.Substring (eq_index + 1, eq_index2 - eq_index - 1);
				name = value.Substring (eq_index2 + 1);
			}

			if (assembly_build_targets.ContainsKey (assembly_name))
				throw ErrorHelper.CreateError (101, "The assembly '{0}' is specified multiple times in --assembly-build-target arguments.", assembly_name);

			AssemblyBuildTarget build_target;
			switch (target) {
			case "staticobject":
				build_target = AssemblyBuildTarget.StaticObject;
				break;
			case "dynamiclibrary":
				build_target = AssemblyBuildTarget.DynamicLibrary;
				break;
			case "framework":
				build_target = AssemblyBuildTarget.Framework;
				break;
			default:
				throw ErrorHelper.CreateError (10, "Could not parse the command line arguments: --assembly-build-target={0}", value);
			}

			assembly_build_targets [assembly_name] = new Tuple<AssemblyBuildTarget, string> (build_target, name);
		}

		void SelectAssemblyBuildTargets ()
		{
			Tuple<AssemblyBuildTarget, string> all = null;
			Tuple<AssemblyBuildTarget, string> sdk = null;
			List<Exception> exceptions = null;

			if (IsSimulatorBuild)
				return;

			// By default each assemblies is compiled to a static object.
			if (assembly_build_targets.Count == 0)
				assembly_build_targets.Add ("@all", new Tuple<AssemblyBuildTarget, string> (AssemblyBuildTarget.StaticObject, ""));

			assembly_build_targets.TryGetValue ("@all", out all);
			assembly_build_targets.TryGetValue ("@sdk", out sdk);

			foreach (var target in Targets) {
				var asm_build_targets = new Dictionary<string, Tuple<AssemblyBuildTarget, string>> (assembly_build_targets);

				foreach (var assembly in target.Assemblies) {
					Tuple<AssemblyBuildTarget, string> build_target;
					var asm_name = assembly.Identity;

					if (asm_build_targets.TryGetValue (asm_name, out build_target)) {
						asm_build_targets.Remove (asm_name);
					} else if (sdk != null && Profile.IsSdkAssembly (asm_name)) {
						build_target = sdk;
					} else {
						build_target = all;
					}

					if (build_target == null) {
						if (exceptions == null)
							exceptions = new List<Exception> ();
						exceptions.Add (ErrorHelper.CreateError (105, "No assembly build target was specified for '{0}'.", assembly.Identity));
						continue;
					}

					assembly.BuildTarget = build_target.Item1;
					// The default build target name is the assembly's filename, including the extension,
					// so that for instance for System.dll, we'd end up with a System.dll.framework
					// (this way it doesn't clash with the system's System.framework).
					assembly.BuildTargetName = string.IsNullOrEmpty (build_target.Item2) ? Path.GetFileName (assembly.FileName) : build_target.Item2;
				}

				foreach (var abt in asm_build_targets) {
					if (abt.Key == "@all" || abt.Key == "@sdk")
						continue;

					if (exceptions == null)
						exceptions = new List<Exception> ();
					exceptions.Add (ErrorHelper.CreateError (99, "The assembly build target '{0}' did not match any assemblies.", abt.Key));
				}

				if (exceptions != null)
					continue;

				var grouped = target.Assemblies.GroupBy ((a) => a.BuildTargetName);
				foreach (var @group in grouped) {
					var assemblies = @group.AsEnumerable ().ToArray ();

					// Check that all assemblies in a group have the same build target
					for (int i = 1; i < assemblies.Length; i++) {
						if (assemblies [0].BuildTarget != assemblies [i].BuildTarget)
							throw ErrorHelper.CreateError (102, "The assemblies '{0}' and '{1}' have the same target name ('{2}'), but different targets ('{3}' and '{4}').",
														   assemblies [0].Identity, assemblies [1].Identity, assemblies [0].BuildTargetName, assemblies [0].BuildTarget, assemblies [1].BuildTarget);
					}

					// Check that static objects must consist of only one assembly
					if (assemblies.Length != 1 && assemblies [0].BuildTarget == AssemblyBuildTarget.StaticObject)
						throw ErrorHelper.CreateError (103, "The static object '{0}' contains more than one assembly ('{1}'), but each static object must correspond with exactly one assembly.",
													   assemblies [0].BuildTargetName, string.Join ("', '", assemblies.Select ((a) => a.Identity).ToArray ()));
				}
			}


			if (exceptions != null)
				throw new AggregateException (exceptions);
		}

		public void SetDlsymOption (string asm, bool dlsym)
		{
			if (DlsymAssemblies == null)
				DlsymAssemblies = new List<Tuple<string, bool>> ();

			DlsymAssemblies.Add (new Tuple<string, bool> (Path.GetFileNameWithoutExtension (asm), dlsym));

			DlsymOptions = DlsymOptions.Custom;
		}

		public void ParseDlsymOptions (string options)
		{
			bool dlsym;
			if (Driver.TryParseBool (options, out dlsym)) {
				DlsymOptions = dlsym ? DlsymOptions.All : DlsymOptions.None;
			} else {
				DlsymAssemblies = new List<Tuple<string, bool>> ();

				var assemblies = options.Split (',');
				foreach (var assembly in assemblies) {
					var asm = assembly;
					if (assembly.StartsWith ("+", StringComparison.Ordinal)) {
						dlsym = true;
						asm = assembly.Substring (1);
					} else if (assembly.StartsWith ("-", StringComparison.Ordinal)) {
						dlsym = false;
						asm = assembly.Substring (1);
					} else {
						dlsym = true;
					}
					DlsymAssemblies.Add (new Tuple<string, bool> (Path.GetFileNameWithoutExtension (asm), dlsym));
				}

				DlsymOptions = DlsymOptions.Custom;
			}
		}

		public bool UseDlsym (string assembly)
		{
			string asm;

			if (DlsymAssemblies != null) {
				asm = Path.GetFileNameWithoutExtension (assembly);
				foreach (var tuple in DlsymAssemblies) {
					if (string.Equals (tuple.Item1, asm, StringComparison.Ordinal))
						return tuple.Item2;
				}
			}

			switch (DlsymOptions) {
			case DlsymOptions.All:
				return true;
			case DlsymOptions.None:
				return false;
			}

			if (EnableLLVMOnlyBitCode)
				return false;

			switch (Platform) {
			case ApplePlatform.iOS:
				return !Profile.IsSdkAssembly (Path.GetFileNameWithoutExtension (assembly));
			case ApplePlatform.TVOS:
			case ApplePlatform.WatchOS:
				return false;
			default:
				throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", Platform);
			}
		}

		public string MonoGCParams {
			get {
				// Configure sgen to use a small nursery
				if (IsTodayExtension) {
					return "nursery-size=512k,soft-heap-limit=8m";
				} else if (Platform == ApplePlatform.WatchOS) {
					// A bit test shows different behavior
					// Sometimes apps are killed with ~100mb allocated,
					// but I've seen apps allocate up to 240+mb as well
					return "nursery-size=512k,soft-heap-limit=8m";
				} else {
					return "nursery-size=512k";
				}
			}
		}

		public bool IsDeviceBuild { 
			get { return BuildTarget == BuildTarget.Device; } 
		}

		public bool IsSimulatorBuild { 
			get { return BuildTarget == BuildTarget.Simulator; } 
		}

		public IEnumerable<Abi> Abis {
			get { return abis; }
		}

		public BitCodeMode BitCodeMode { get; set; }

		public bool EnableAsmOnlyBitCode { get { return BitCodeMode == BitCodeMode.ASMOnly; } }
		public bool EnableLLVMOnlyBitCode { get { return BitCodeMode == BitCodeMode.LLVMOnly; } }
		public bool EnableMarkerOnlyBitCode { get { return BitCodeMode == BitCodeMode.MarkerOnly; } }
		public bool EnableBitCode { get { return BitCodeMode != BitCodeMode.None; } }

		public ICollection<Abi> AllArchitectures {
			get {
				if (all_architectures == null) {
					all_architectures = new HashSet<Abi> ();
					foreach (var abi in abis)
						all_architectures.Add (abi & Abi.ArchMask);
					foreach (var ext in Extensions) {
						var executable = GetStringFromInfoPList (ext, "CFBundleExecutable");
						if (string.IsNullOrEmpty (executable))
							throw ErrorHelper.CreateError (63, "Cannot find the executable in the extension {0} (no CFBundleExecutable entry in its Info.plist)", ext);
						foreach (var abi in Xamarin.MachO.GetArchitectures (Path.Combine (ext, executable)))
							all_architectures.Add (abi);
					}
				}
				return all_architectures;
			}
		}

		public bool IsTodayExtension {
			get {
				return ExtensionIdentifier == "com.apple.widget-extension";
			}
		}

		public bool IsWatchExtension {
			get {
				return ExtensionIdentifier == "com.apple.watchkit";
			}
		}

		public bool IsTVExtension {
			get {
				return ExtensionIdentifier == "com.apple.tv-services";
			}
		}

		public string ExtensionIdentifier {
			get {
				if (!IsExtension)
					return null;

				var info_plist = Path.Combine (AppDirectory, "Info.plist");
				var plist = Driver.FromPList (info_plist);
				var dict = plist.Get<PDictionary> ("NSExtension");
				if (dict == null)
					return null;
				return dict.GetString ("NSExtensionPointIdentifier");
			}
		}

		public string BundleId {
			get {
				return GetStringFromInfoPList ("CFBundleIdentifier");
			}
		}

		string GetStringFromInfoPList (string key)
		{
			return GetStringFromInfoPList (AppDirectory, key);
		}

		string GetStringFromInfoPList (string directory, string key)
		{
			var info_plist = Path.Combine (directory, "Info.plist");
			if (!File.Exists (info_plist))
				return null;

			var plist = Driver.FromPList (info_plist);
			if (!plist.ContainsKey (key))
				return null;
			return plist.GetString (key);
		}

		public void SetDefaultAbi ()
		{
			if (abis == null)
				abis = new List<Abi> ();
			
			switch (Platform) {
			case ApplePlatform.iOS:
				if (abis.Count == 0) {
					abis.Add (IsDeviceBuild ? Abi.ARMv7 : Abi.i386);
				}
				break;
			case ApplePlatform.WatchOS:
				if (abis.Count == 0)
					throw ErrorHelper.CreateError (76, "No architecture specified (using the --abi argument). An architecture is required for {0} projects.", "Xamarin.WatchOS");
				break;
			case ApplePlatform.TVOS:
				if (abis.Count == 0)
					throw ErrorHelper.CreateError (76, "No architecture specified (using the --abi argument). An architecture is required for {0} projects.", "Xamarin.TVOS");
				break;
			default:
				throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", Platform);
			}
		}

		public void ValidateAbi ()
		{
			var validAbis = new List<Abi> ();
			switch (Platform) {
			case ApplePlatform.iOS:
				if (IsDeviceBuild) {
					validAbis.Add (Abi.ARMv7);
					validAbis.Add (Abi.ARMv7 | Abi.Thumb);
					validAbis.Add (Abi.ARMv7 | Abi.LLVM);
					validAbis.Add (Abi.ARMv7 | Abi.LLVM | Abi.Thumb);
					validAbis.Add (Abi.ARMv7s);
					validAbis.Add (Abi.ARMv7s | Abi.Thumb);
					validAbis.Add (Abi.ARMv7s | Abi.LLVM);
					validAbis.Add (Abi.ARMv7s | Abi.LLVM | Abi.Thumb);
				} else {
					validAbis.Add (Abi.i386);
				}
				if (IsDeviceBuild) {
					validAbis.Add (Abi.ARM64);
					validAbis.Add (Abi.ARM64 | Abi.LLVM);
				} else {
					validAbis.Add (Abi.x86_64);
				}
				break;
			case ApplePlatform.WatchOS:
				if (IsDeviceBuild) {
					validAbis.Add (Abi.ARMv7k);
					validAbis.Add (Abi.ARMv7k | Abi.LLVM);
				} else {
					validAbis.Add (Abi.i386);
				}
				break;
			case ApplePlatform.TVOS:
				if (IsDeviceBuild) {
					validAbis.Add (Abi.ARM64);
					validAbis.Add (Abi.ARM64 | Abi.LLVM);
				} else {
					validAbis.Add (Abi.x86_64);
				}
				break;
			default:
				throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", Platform);
			}

			foreach (var abi in abis) {
				if (!validAbis.Contains (abi))
					throw ErrorHelper.CreateError (75, "Invalid architecture '{0}' for {1} projects. Valid architectures are: {2}", abi, Platform, string.Join (", ", validAbis.Select ((v) => v.AsString ()).ToArray ()));
			}
		}

		public void ClearAbi ()
		{
			abis = null;
		}

		// This is to load the symbols for all assemblies, so that we can give better error messages
		// (with file name / line number information).
		public void LoadSymbols ()
		{
			foreach (var t in Targets)
				t.LoadSymbols ();
		}

		public void ParseAbi (string abi)
		{
			var res = new List<Abi> ();
			foreach (var str in abi.Split (new char [] { ',' }, StringSplitOptions.RemoveEmptyEntries)) {
				Abi value;
				switch (str) {
				case "i386":
					value = Abi.i386;
					break;
				case "x86_64":
					value = Abi.x86_64;
					break;
				case "armv7":
					value = Abi.ARMv7;
					break;
				case "armv7+llvm":
					value = Abi.ARMv7 | Abi.LLVM;
					break;
				case "armv7+llvm+thumb2":
					value = Abi.ARMv7 | Abi.LLVM | Abi.Thumb;
					break;
				case "armv7s":
					value = Abi.ARMv7s;
					break;
				case "armv7s+llvm":
					value = Abi.ARMv7s | Abi.LLVM;
					break;
				case "armv7s+llvm+thumb2":
					value = Abi.ARMv7s | Abi.LLVM | Abi.Thumb;
					break;
				case "arm64":
					value = Abi.ARM64;
					break;
				case "arm64+llvm":
					value = Abi.ARM64 | Abi.LLVM;
					break;
				case "armv7k":
					value = Abi.ARMv7k;
					break;
				case "armv7k+llvm":
					value = Abi.ARMv7k | Abi.LLVM;
					break;
				default:
					throw new MonoTouchException (15, true, "Invalid ABI: {0}. Supported ABIs are: i386, x86_64, armv7, armv7+llvm, armv7+llvm+thumb2, armv7s, armv7s+llvm, armv7s+llvm+thumb2, armv7k, armv7k+llvm, arm64 and arm64+llvm.", str);
				}

				// merge this value with any existing ARMv? already specified.
				// this is so that things like '--armv7 --thumb' work correctly.
				if (abis != null) {
					for (int i = 0; i < abis.Count; i++) {
						if ((abis [i] & Abi.ArchMask) == (value & Abi.ArchMask)) {
							value |= abis [i];
							break;
						}
					}
				}

				res.Add (value);
			}

			// We replace any existing abis, to keep the old behavior where '--armv6 --armv7' would 
			// enable only the last abi specified and disable the rest.
			abis = res;
		}

		public static string GetArchitectures (IEnumerable<Abi> abis)
		{
			var res = new List<string> ();

			foreach (var abi in abis)
				res.Add (abi.AsArchString ());

			return string.Join (", ", res.ToArray ());
		}

		public bool IsArchEnabled (Abi arch)
		{
			return IsArchEnabled (abis, arch);
		}

		public static bool IsArchEnabled (IEnumerable<Abi> abis, Abi arch)
		{
			foreach (var abi in abis) {
				if ((abi & arch) != 0)
					return true;
			}
			return false;
		}

		public void Build ()
		{
			if (Driver.Force) {
				Driver.Log (3, "A full rebuild has been forced by the command line argument -f.");
				Cache.Clean ();
			} else {
				// this will destroy the cache if invalid, which makes setting Driver.Force to true mostly unneeded
				// in fact setting it means some actions (like extract native resource) gets duplicate for fat builds
				Cache.VerifyCache ();
			}

			Initialize ();
			ValidateAbi ();
			SelectRegistrar ();
			ExtractNativeLinkInfo ();
			SelectNativeCompiler ();
			ProcessAssemblies ();

			// Everything that can be parallelized is put into a list of tasks,
			// which are then executed at the end.
			build_tasks = new BuildTasks ();

			Driver.Watch ("Generating build tasks", 1);
			CompilePInvokeWrappers ();
			BuildApp (build_tasks);

			if (Driver.Dot)
				build_tasks.Dot (Path.Combine (Cache.Location, "build.dot"));

			Driver.Watch ("Building build tasks", 1);
			build_tasks.Execute ();

			// TODO: make more of the below actions parallelizable.

			BuildBundle ();
			BuildDsymDirectory ();
			BuildMSymDirectory ();
			StripNativeCode ();
			BundleAssemblies ();

			WriteNotice ();
			GenerateRuntimeOptions ();

			if (Cache.IsCacheTemporary) {
				// If we used a temporary directory we created ourselves for the cache
				// (in which case it's more a temporary location where we store the 
				// temporary build products than a cache), it will not be used again,
				// so just delete it.
				try {
					Directory.Delete (Cache.Location, true);
				} catch {
					// Don't care.
				}
			} else {
				// Write the cache data as the last step, so there is no half-done/incomplete (but yet detected as valid) cache.
				Cache.ValidateCache ();
			}

			Console.WriteLine ("{0} built successfully.", AppDirectory);
		}

		bool no_framework;
		public void SetDefaultFramework ()
		{
			// If no target framework was specified, check if we're referencing Xamarin.iOS.dll.
			// It's an error if neither target framework nor Xamarin.iOS.dll is not specified
			if (!Driver.HasTargetFramework) {
				foreach (var reference in References) {
					var name = Path.GetFileName (reference);
					switch (name) {
					case "Xamarin.iOS.dll":
						Driver.TargetFramework = TargetFramework.Xamarin_iOS_1_0;
						break;
					case "Xamarin.TVOS.dll":
					case "Xamarin.WatchOS.dll":
						throw ErrorHelper.CreateError (86, "A target framework (--target-framework) must be specified when building for TVOS or WatchOS.");
					}

					if (Driver.HasTargetFramework)
						break;
				}
			}

			if (!Driver.HasTargetFramework) {
				// Set a default target framework to show errors in the least confusing order.
				Driver.TargetFramework = TargetFramework.Xamarin_iOS_1_0;
				no_framework = true;
			}
		}

		void Initialize ()
		{
			if (EnableDebug && IsLLVM)
				ErrorHelper.Warning (3003, "Debugging is not supported when building with LLVM. Debugging has been disabled.");

			if (!IsLLVM && (EnableAsmOnlyBitCode || EnableLLVMOnlyBitCode))
				throw ErrorHelper.CreateError (3008, "Bitcode support requires the use of LLVM (--abi=arm64+llvm etc.)");

			if (EnableDebug) {
				if (!DebugTrack.HasValue) {
					DebugTrack = IsSimulatorBuild;
				}
			} else {
				if (DebugTrack.HasValue) {
					ErrorHelper.Warning (32, "The option '--debugtrack' is ignored unless '--debug' is also specified.");
				}
				DebugTrack = false;
			}

			if (EnableAsmOnlyBitCode)
				LLVMAsmWriter = true;

			if (!File.Exists (RootAssembly))
				throw new MonoTouchException (7, true, "The root assembly '{0}' does not exist", RootAssembly);
			
			if (no_framework)
				throw ErrorHelper.CreateError (96, "No reference to Xamarin.iOS.dll was found.");

			// Add a reference to the platform assembly if none has been added, and check that we're not referencing
			// any platform assemblies from another platform.
			var platformAssemblyReference = false;
			foreach (var reference in References) {
				var name = Path.GetFileNameWithoutExtension (reference);
				if (name == Driver.GetProductAssembly (this)) {
					platformAssemblyReference = true;
				} else {
					switch (name) {
					case "Xamarin.iOS":
					case "Xamarin.TVOS":
					case "Xamarin.WatchOS":
						throw ErrorHelper.CreateError (41, "Cannot reference '{0}' in a {1} app.", Path.GetFileName (reference), Driver.TargetFramework.Identifier);
					}
				}
			}
			if (!platformAssemblyReference) {
				ErrorHelper.Warning (85, "No reference to '{0}' was found. It will be added automatically.", Driver.GetProductAssembly (this) + ".dll");
				References.Add (Path.Combine (Driver.GetPlatformFrameworkDirectory (this), Driver.GetProductAssembly (this) + ".dll"));
			}

			var FrameworkDirectory = Driver.GetPlatformFrameworkDirectory (this);
			var RootDirectory = Path.GetDirectoryName (Path.GetFullPath (RootAssembly));

			((MonoTouchProfile) Profile.Current).SetProductAssembly (Driver.GetProductAssembly (this));

			string root_wo_ext = Path.GetFileNameWithoutExtension (RootAssembly);
			if (Profile.IsSdkAssembly (root_wo_ext) || Profile.IsProductAssembly (root_wo_ext))
				throw new MonoTouchException (3, true, "Application name '{0}.exe' conflicts with an SDK or product assembly (.dll) name.", root_wo_ext);

			if (!IsDualBuild && !IsExtension) {
				// There might be other/more architectures in extensions than in the main app.
				// If we're building frameworks, we need to build for all architectures, including any in extensions.
				if (HasFrameworks) {
					foreach (var abi in AllArchitectures) {
						if (abis.Contains (abi))
							continue;
						
						abis.Add (abi);
						ErrorHelper.Warning (104, "There is at least one extension that builds for '{0}'. The main app must also build for this architecture if assemblies are compiled into frameworks, so it has automatically been enabled.", abi.AsString ());
					}
				}
			}

			if (IsDualBuild) {
				var target32 = new Target (this);
				var target64 = new Target (this);

				target32.ArchDirectory = Path.Combine (Cache.Location, "32");
				target32.TargetDirectory = IsSimulatorBuild ? Path.Combine (AppDirectory, ".monotouch-32") : Path.Combine (target32.ArchDirectory, "Output");
				target32.AppTargetDirectory = Path.Combine (AppDirectory, ".monotouch-32");
				target32.Resolver.ArchDirectory = Driver.GetArch32Directory (this);
				target32.Abis = SelectAbis (abis, Abi.Arch32Mask);

				target64.ArchDirectory = Path.Combine (Cache.Location, "64");
				target64.TargetDirectory = IsSimulatorBuild ? Path.Combine (AppDirectory, ".monotouch-64") : Path.Combine (target64.ArchDirectory, "Output");
				target64.AppTargetDirectory = Path.Combine (AppDirectory, ".monotouch-64");
				target64.Resolver.ArchDirectory = Driver.GetArch64Directory (this);
				target64.Abis = SelectAbis (abis, Abi.Arch64Mask);

				Targets.Add (target64);
				Targets.Add (target32);
			} else {
				var target = new Target (this);

				target.TargetDirectory = AppDirectory;
				target.AppTargetDirectory = IsSimulatorBuild ? AppDirectory : Path.Combine (AppDirectory, Is64Build ? ".monotouch-64" : ".monotouch-32");
				target.ArchDirectory = Cache.Location;
				target.Resolver.ArchDirectory = Path.Combine (FrameworkDirectory, "..", "..", Is32Build ? "32bits" : "64bits");
				target.Abis = abis;

				Targets.Add (target);

				// Make sure there aren't any lingering .monotouch-* directories.
				if (IsSimulatorBuild) {
					var dir = Path.Combine (AppDirectory, ".monotouch-32");
					if (Directory.Exists (dir))
						Directory.Delete (dir, true);
					dir = Path.Combine (AppDirectory, ".monotouch-64");
					if (Directory.Exists (dir))
						Directory.Delete (dir, true);
				}
			}

			foreach (var target in Targets) {
				target.Resolver.FrameworkDirectory = FrameworkDirectory;
				target.Resolver.RootDirectory = RootDirectory;
				target.Resolver.EnableRepl = EnableRepl;
				target.ManifestResolver.EnableRepl = EnableRepl;
				target.ManifestResolver.FrameworkDirectory = target.Resolver.FrameworkDirectory;
				target.ManifestResolver.RootDirectory = target.Resolver.RootDirectory;
				target.ManifestResolver.ArchDirectory = target.Resolver.ArchDirectory;
				target.Initialize (target == Targets [0]);

				if (!Directory.Exists (target.TargetDirectory))
					Directory.CreateDirectory (target.TargetDirectory);
			}

			if (string.IsNullOrEmpty (ExecutableName)) {
				var bundleExecutable = GetStringFromInfoPList ("CFBundleExecutable");
				ExecutableName = bundleExecutable ?? Path.GetFileNameWithoutExtension (RootAssembly);
			}

			if (ExecutableName != Path.GetFileNameWithoutExtension (AppDirectory))
				ErrorHelper.Warning (30, "The executable name ({0}) and the app name ({1}) are different, this may prevent crash logs from getting symbolicated properly.",
					ExecutableName, Path.GetFileName (AppDirectory));
			
			if (IsExtension && Platform == ApplePlatform.iOS && SdkVersion < new Version (8, 0))
				throw new MonoTouchException (45, true, "--extension is only supported when using the iOS 8.0 (or later) SDK.");

			if (IsExtension && Platform != ApplePlatform.iOS && Platform != ApplePlatform.WatchOS && Platform != ApplePlatform.TVOS)
				throw new MonoTouchException (72, true, "Extensions are not supported for the platform '{0}'.", Platform);

			if (!IsExtension && Platform == ApplePlatform.WatchOS)
				throw new MonoTouchException (77, true, "WatchOS projects must be extensions.");
		
#if ENABLE_BITCODE_ON_IOS
			if (Platform == ApplePlatform.iOS)
				DeploymentTarget = new Version (9, 0);
#endif

			if (DeploymentTarget == null) {
				DeploymentTarget = Xamarin.SdkVersions.GetVersion (Platform);
			} else if (DeploymentTarget < Xamarin.SdkVersions.GetMinVersion (Platform)) {
				throw new MonoTouchException (73, true, "Xamarin.iOS {0} does not support a deployment target of {1} for {3} (the minimum is {2}). Please select a newer deployment target in your project's Info.plist.", Constants.Version, DeploymentTarget, Xamarin.SdkVersions.GetMinVersion (Platform), PlatformName);
			} else if (DeploymentTarget > Xamarin.SdkVersions.GetVersion (Platform)) {
				throw new MonoTouchException (74, true, "Xamarin.iOS {0} does not support a deployment target of {1} for {3} (the maximum is {2}). Please select an older deployment target in your project's Info.plist or upgrade to a newer version of Xamarin.iOS.", Constants.Version, DeploymentTarget, Xamarin.SdkVersions.GetVersion (Platform), PlatformName);
			}

			if (Platform == ApplePlatform.iOS && (HasDynamicLibraries || HasFrameworks) && DeploymentTarget.Major < 8) {
				ErrorHelper.Warning (78, "Incremental builds are enabled with a deployment target < 8.0 (currently {0}). This is not supported (the resulting application will not launch on iOS 9), so the deployment target will be set to 8.0.", DeploymentTarget);
				DeploymentTarget = new Version (8, 0);
			}

			if (!package_mdb.HasValue) {
				package_mdb = EnableDebug;
			} else if (package_mdb.Value && IsLLVM) {
				ErrorHelper.Warning (3007, "Debug info files (*.mdb) will not be loaded when llvm is enabled.");
			}

			if (!enable_msym.HasValue)
				enable_msym = !EnableDebug && IsDeviceBuild;

			if (!UseMonoFramework.HasValue && DeploymentTarget >= new Version (8, 0)) {
				if (IsExtension) {
					UseMonoFramework = true;
					Driver.Log (2, "Automatically linking with Mono.framework because this is an extension");
				} else if (Extensions.Count > 0) {
					UseMonoFramework = true;
					Driver.Log (2, "Automatically linking with Mono.framework because this is an app with extensions");
				}
			}

			if (!UseMonoFramework.HasValue)
				UseMonoFramework = false;
			
			if (UseMonoFramework.Value)
				Frameworks.Add (Path.Combine (Driver.GetProductFrameworksDirectory (this), "Mono.framework"));

			if (!PackageMonoFramework.HasValue) {
				if (!IsExtension && Extensions.Count > 0 && !UseMonoFramework.Value) {
					// The main app must package the Mono framework if we have extensions, even if it's not linking with
					// it. This happens when deployment target < 8.0 for the main app.
					PackageMonoFramework = true;
				} else {
					// Package if we're not an extension and we're using the mono framework.
					PackageMonoFramework = UseMonoFramework.Value && !IsExtension;
				}
			}

			if (Frameworks.Count > 0) {
				switch (Platform) {
				case ApplePlatform.iOS:
					if (DeploymentTarget < new Version (8, 0))
						throw ErrorHelper.CreateError (65, "Xamarin.iOS only supports embedded frameworks when deployment target is at least 8.0 (current deployment target: '{0}'; embedded frameworks: '{1}')", DeploymentTarget, string.Join (", ", Frameworks.ToArray ()));
					break;
				case ApplePlatform.WatchOS:
					if (DeploymentTarget < new Version (2, 0))
						throw ErrorHelper.CreateError (65, "Xamarin.iOS only supports embedded frameworks when deployment target is at least 2.0 (current deployment target: '{0}'; embedded frameworks: '{1}')", DeploymentTarget, string.Join (", ", Frameworks.ToArray ()));
					break;
				case ApplePlatform.TVOS:
					// All versions of tvOS support extensions
					break;
				default:
					throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", Platform);
				}
			}

			if (IsDeviceBuild) {
				switch (BitCodeMode) {
				case BitCodeMode.ASMOnly:
					if (Platform == ApplePlatform.WatchOS)
						throw ErrorHelper.CreateError (83, "asm-only bitcode is not supported on watchOS. Use either --bitcode:marker or --bitcode:full.");
					break;
				case BitCodeMode.LLVMOnly:
				case BitCodeMode.MarkerOnly:
					break;
				case BitCodeMode.None:
					// If neither llvmonly nor asmonly is enabled, enable markeronly.
					if (Platform == ApplePlatform.TVOS || Platform == ApplePlatform.WatchOS)
						BitCodeMode = BitCodeMode.MarkerOnly;
					break;
				}
			}

			if (EnableBitCode && IsSimulatorBuild)
				throw ErrorHelper.CreateError (84, "Bitcode is not supported in the simulator. Do not pass --bitcode when building for the simulator.");

			if (LinkMode == LinkMode.None && SdkVersion < SdkVersions.GetVersion (Platform))
				throw ErrorHelper.CreateError (91, "This version of Xamarin.iOS requires the {0} {1} SDK (shipped with Xcode {2}) when the managed linker is disabled. Either upgrade Xcode, or enable the managed linker by changing the Linker behaviour to Link Framework SDKs Only.", PlatformName, SdkVersions.GetVersion (Platform), SdkVersions.Xcode);

			if (HasFrameworks || UseMonoFramework.Value) {
				LibMonoLinkMode = AssemblyBuildTarget.Framework;
			} else if (HasDynamicLibraries) {
				LibMonoLinkMode = AssemblyBuildTarget.DynamicLibrary;
			}

			if (HasFrameworks) {
				LibXamarinLinkMode = AssemblyBuildTarget.Framework;
			} else if (HasDynamicLibraries) {
				LibXamarinLinkMode = AssemblyBuildTarget.DynamicLibrary;
			}

			Namespaces.Initialize ();

			InitializeCommon ();

			Driver.Watch ("Resolve References", 1);
		}
		
		void SelectRegistrar ()
		{
			// If the default values are changed, remember to update CanWeSymlinkTheApplication
			// and main.m (default value for xamarin_use_old_dynamic_registrar must match).
			if (Registrar == RegistrarMode.Default) {
				if (IsDeviceBuild) {
					Registrar = RegistrarMode.Static;
				} else { /* if (app.IsSimulatorBuild) */
					Registrar = RegistrarMode.Dynamic;
				}
			}

			foreach (var target in Targets)
				target.SelectStaticRegistrar ();
		}

		// Select all abi from the list matching the specified mask.
		List<Abi> SelectAbis (IEnumerable<Abi> abis, Abi mask)
		{
			var rv = new List<Abi> ();
			foreach (var abi in abis) {
				if ((abi & mask) != 0)
					rv.Add (abi);
			}
			return rv;
		}

		public string AssemblyName {
			get {
				return Path.GetFileName (RootAssembly);
			}
		}

		public string Executable {
			get {
				return Path.Combine (AppDirectory, ExecutableName);
			}
		}

		void ProcessAssemblies ()
		{
			// This can be parallelized once we determine the linker doesn't use any static state.
			foreach (var target in Targets) {
				if (target.CanWeSymlinkTheApplication ()) {
					target.Symlink ();
				} else {
					target.ProcessAssemblies ();
				}
			}

			// Deduplicate files from the Build directory. We need to do this before the AOT
			// step, so that we can ignore timestamp/GUID in assemblies (the GUID is
			// burned into the AOT assembly, so after that we'll need the original assembly.
			if (IsDualBuild && IsDeviceBuild) {
				// All the assemblies are now in BuildDirectory.
				var t1 = Targets [0];
				var t2 = Targets [1];

				foreach (var f1 in Directory.GetFileSystemEntries (t1.BuildDirectory)) {
					var f2 = Path.Combine (t2.BuildDirectory, Path.GetFileName (f1));
					if (!File.Exists (f2))
						continue;
					var ext = Path.GetExtension (f1).ToUpperInvariant ();
					var is_assembly = ext == ".EXE" || ext == ".DLL";
					if (!is_assembly)
						continue;

					if (!Cache.CompareAssemblies (f1, f2, true))
						continue;
						
					if (Driver.Verbosity > 0)
						Console.WriteLine ("Targets {0} and {1} found to be identical", f1, f2);
					// Don't use symlinks, since it just gets more complicated
					// For instance: on rebuild, when should the symlink be updated and when
					// should the target of the symlink be updated? And all the usages
					// must be audited to ensure the right thing is done...
					Driver.CopyAssembly (f1, f2);
				}
			}
		}

		void CompilePInvokeWrappers ()
		{
			foreach (var target in Targets)
				target.CompilePInvokeWrappers ();
		}

		void BuildApp (BuildTasks build_tasks)
		{
			SelectAssemblyBuildTargets (); // This must be done after the linker has run, since the linker may bring in more assemblies than only those referenced explicitly.

			foreach (var target in Targets) {
				if (target.CanWeSymlinkTheApplication ())
					continue;

				target.ComputeLinkerFlags ();
				target.Compile ();
				target.NativeLink (build_tasks);
			}
		}

		void WriteNotice ()
		{
			if (!IsDeviceBuild)
				return;

			if (Directory.Exists (Path.Combine (AppDirectory, "NOTICE")))
				throw new MonoTouchException (1016, true, "Failed to create the NOTICE file because a directory already exists with the same name.");

			try {
				// write license information inside the .app
				StringBuilder sb = new StringBuilder ();
				sb.Append ("Xamarin built applications contain open source software.  ");
				sb.Append ("For detailed attribution and licensing notices, please visit...");
				sb.AppendLine ().AppendLine ().Append ("http://xamarin.com/mobile-licensing").AppendLine ();
				Driver.WriteIfDifferent (Path.Combine (AppDirectory, "NOTICE"), sb.ToString ());
			} catch (Exception ex) {
				throw new MonoTouchException (1017, true, ex, "Failed to create the NOTICE file: {0}", ex.Message);
			}
		}

		public static void CopyMSymData (string src, string dest)
		{
			if (string.IsNullOrEmpty (src) || string.IsNullOrEmpty (dest))
				return;
			if (!Directory.Exists (src)) // got no aot data
				return;

			var p = new Process ();
			p.StartInfo.UseShellExecute = false;
			p.StartInfo.RedirectStandardError = true;
			p.StartInfo.FileName = "mono-symbolicate";
			p.StartInfo.Arguments = $"store-symbols \"{src}\" \"{dest}\"";

			try {
				if (p.Start ()) {
					var error = p.StandardError.ReadToEnd();
					p.WaitForExit ();
					GC.Collect (); // Workaround for: https://bugzilla.xamarin.com/show_bug.cgi?id=43462#c14
					if (p.ExitCode == 0)
						return;
					else {
						ErrorHelper.Warning (95, $"Aot files could not be copied to the destination directory {dest}: {error}"); 
						return;
					}
				}

				ErrorHelper.Warning (95, $"Aot files could not be copied to the destination directory {dest}: Could not start process."); 
				return;
			}
			catch (Exception e) {
				ErrorHelper.Warning (95, e, $"Aot files could not be copied to the destination directory {dest}: Could not start process."); 
				return;
			}
		}

		void BuildBundle ()
		{
			Driver.Watch ("Building app bundle", 1);

			var bundle_files = new Dictionary<string, BundleFileInfo> ();

			// Make sure we bundle Mono.framework if we need to.
			if (PackageMonoFramework == true) {
				BundleFileInfo info;
				var name = "Frameworks/Mono.framework";
				bundle_files [name] = info = new BundleFileInfo ();
				info.Sources.Add (GetLibMono (AssemblyBuildTarget.Framework));
			}

			// Collect files to bundle from every target
			if (Targets.Count == 1) {
				bundle_files = Targets [0].BundleFiles;
			} else {
				foreach (var target in Targets) {
					foreach (var kvp in target.BundleFiles) {
						BundleFileInfo info;
						if (!bundle_files.TryGetValue (kvp.Key, out info))
							bundle_files [kvp.Key] = info = new BundleFileInfo () { DylibToFramework = kvp.Value.DylibToFramework };
						info.Sources.UnionWith (kvp.Value.Sources);
					}
				}
			}

			// And from ourselves
			var all_assemblies = Targets.SelectMany ((v) => v.Assemblies);
			var all_frameworks = Frameworks.Concat (all_assemblies.SelectMany ((v) => v.Frameworks));
			var all_weak_frameworks = WeakFrameworks.Concat (all_assemblies.SelectMany ((v) => v.WeakFrameworks));
			foreach (var fw in all_frameworks.Concat (all_weak_frameworks)) {
				BundleFileInfo info;
				if (!Path.GetFileName (fw).EndsWith (".framework", StringComparison.Ordinal))
					continue;
				var key = $"Frameworks/{Path.GetFileName (fw)}";
				if (!bundle_files.TryGetValue (key, out info))
					bundle_files [key] = info = new BundleFileInfo ();
				info.Sources.Add (fw);
			}

			// Copy frameworks to the app bundle.
			if (IsExtension && !IsWatchExtension) {
				// In extensions we need to save a list of the frameworks we need so that the main app can bundle them.
				var sb = new StringBuilder ();
				foreach (var key in bundle_files.Keys.ToArray ()) {
					if (!Path.GetFileName (key).EndsWith (".framework", StringComparison.Ordinal))
						continue;
					var value = bundle_files [key];
					foreach (var src in value.Sources)
						sb.AppendLine ($"{key}:{src}");
					bundle_files.Remove (key);
				}
				if (sb.Length > 0)
					Driver.WriteIfDifferent (Path.Combine (Path.GetDirectoryName (AppDirectory), "frameworks.txt"), sb.ToString ());
			} else {
				// Load any frameworks extensions saved just above
				foreach (var appex in Extensions) {
					var f_path = Path.Combine (appex, "..", "frameworks.txt");
					if (!File.Exists (f_path))
						continue;

					foreach (var fw in File.ReadAllLines (f_path)) {
						Driver.Log (3, "Copying {0} to the app's Frameworks directory because it's used by the extension {1}", fw, Path.GetFileName (appex));
						var colon = fw.IndexOf (':');
						if (colon == -1)
							throw new Exception ();
						var key = fw.Substring (0, colon);
						var name = fw.Substring (colon + 1);
						BundleFileInfo info;
						if (!bundle_files.TryGetValue (key, out info))
							bundle_files [key] = info = new BundleFileInfo ();
						info.Sources.Add (name);
						if (name.EndsWith (".dylib", StringComparison.Ordinal))
							info.DylibToFramework = true;
					}
				}
			}

			foreach (var kvp in bundle_files) {
				var name = kvp.Key;
				var info = kvp.Value;
				var targetPath = Path.Combine (AppDirectory, name);
				var files = info.Sources;

				if (Directory.Exists (files.First ())) {
					if (files.Count != 1)
						throw ErrorHelper.CreateError (99, "Internal error: 'can't lipo directories'. Please file a bug report with a test case (http://bugzilla.xamarin.com).");
					if (info.DylibToFramework)
						throw ErrorHelper.CreateError (99, "Internal error: 'can't convert frameworks to frameworks'. Please file a bug report with a test case (http://bugzilla.xamarin.com).");
					var framework_src = files.First ();
					var framework_filename = Path.Combine (framework_src, Path.GetFileNameWithoutExtension (framework_src));
					if (!MachO.IsDynamicFramework (framework_filename)) {
						Driver.Log (1, "The framework {0} is a framework of static libraries, and will not be copied to the app.", framework_src);
					} else {
						UpdateDirectory (framework_src, Path.GetDirectoryName (targetPath));
						if (IsDeviceBuild) {
							// Remove architectures we don't care about.
							MachO.SelectArchitectures (Path.Combine (targetPath, Path.GetFileNameWithoutExtension (framework_src)), AllArchitectures);
						}
					}
				} else {
					var targetDirectory = Path.GetDirectoryName (targetPath);
					if (!IsUptodate (files, new string [] { targetPath })) {
						Directory.CreateDirectory (targetDirectory);
						if (files.Count == 1) {
							CopyFile (files.First (), targetPath);
						} else {
							var sb = new StringBuilder ();
							foreach (var lib in files) {
								sb.Append (Driver.Quote (lib));
								sb.Append (' ');
							}
							sb.Append ("-create -output ");
							sb.Append (Driver.Quote (targetPath));
							Driver.RunLipo (sb.ToString ());
						}
						if (LibMonoLinkMode == AssemblyBuildTarget.Framework)
							Driver.XcodeRun ("install_name_tool", "-change @executable_path/libmonosgen-2.0.dylib @rpath/Mono.framework/Mono " + Driver.Quote (targetPath));
					} else {
						Driver.Log (3, "Target '{0}' is up-to-date.", targetPath);
					}

					if (info.DylibToFramework) {
						var bundleName = Path.GetFileName (name);
						CreateFrameworkInfoPList (Path.Combine (targetDirectory, "Info.plist"), bundleName, BundleId + Path.GetFileNameWithoutExtension (bundleName), bundleName);
					}
				}
			}

			// If building a fat app, we need to lipo the two different executables we have together
			if (IsDeviceBuild) {
				if (IsDualBuild) {
					if (IsUptodate (new string [] { Targets [0].Executable, Targets [1].Executable }, new string [] { Executable })) {
						cached_executable = true;
						Driver.Log (3, "Target '{0}' is up-to-date.", Executable);
					} else {
						var cmd = new StringBuilder ();
						foreach (var target in Targets) {
							cmd.Append (Driver.Quote (target.Executable));
							cmd.Append (' ');
						}
						cmd.Append ("-create -output ");
						cmd.Append (Driver.Quote (Executable));
						Driver.RunLipo (cmd.ToString ());
					}
				} else {
					cached_executable = Targets [0].CachedExecutable;
				}
			}
		}
			
		public void ExtractNativeLinkInfo ()
		{
			var exceptions = new List<Exception> ();

			foreach (var target in Targets)
				target.ExtractNativeLinkInfo (exceptions);

			if (exceptions.Count > 0)
				throw new AggregateException (exceptions);

			Driver.Watch ("Extracted native link info", 1);
		}

		public void SelectNativeCompiler ()
		{
			foreach (var t in Targets) {
				foreach (var a in t.Assemblies) {
					if (a.EnableCxx) {	
						EnableCxx = true;
						break;
					}
				}
			}

			Driver.CalculateCompilerPath (this);
		}

		public string GetLibMono (AssemblyBuildTarget build_target)
		{
			switch (build_target) {
			case AssemblyBuildTarget.StaticObject:
				return Path.Combine (Driver.GetMonoTouchLibDirectory (this), "libmonosgen-2.0.a");
			case AssemblyBuildTarget.DynamicLibrary:
				return Path.Combine (Driver.GetMonoTouchLibDirectory (this), "libmonosgen-2.0.dylib");
			case AssemblyBuildTarget.Framework:
				return Path.Combine (Driver.GetProductSdkDirectory (this), "Frameworks", "Mono.framework");
			default:
				throw ErrorHelper.CreateError (100, "Invalid assembly build target: '{0}'. Please file a bug report with a test case (http://bugzilla.xamarin.com).", build_target);
			}
		}

		public string GetLibXamarin (AssemblyBuildTarget build_target)
		{
			switch (build_target) {
			case AssemblyBuildTarget.StaticObject:
				return Path.Combine (Driver.GetMonoTouchLibDirectory (this), EnableDebug ? "libxamarin-debug.a" : "libxamarin.a");
			case AssemblyBuildTarget.DynamicLibrary:
				return Path.Combine (Driver.GetMonoTouchLibDirectory (this), EnableDebug ? "libxamarin-debug.dylib" : "libxamarin.dylib");
			case AssemblyBuildTarget.Framework:
				return Path.Combine (Driver.GetProductSdkDirectory (this), "Frameworks", EnableDebug ? "Xamarin-debug.framework" : "Xamarin.framework");
			default:
				throw ErrorHelper.CreateError (100, "Invalid assembly build target: '{0}'. Please file a bug report with a test case (http://bugzilla.xamarin.com).", build_target);
			}
		}

		public void NativeLink (BuildTasks build_tasks)
		{
			foreach (var target in Targets)
				target.NativeLink (build_tasks);
		}
		
		// this will filter/remove warnings that are not helpful (e.g. complaining about non-matching armv6-6 then armv7-6 on fat binaries)
		// and turn the remaining of the warnings into MT5203 that MonoDevelop will be able to report as real warnings (not just logs)
		// it will also look for symbol-not-found errors and try to provide useful error messages.
		public static void ProcessNativeLinkerOutput (Target target, string output, IList<string> inputs, List<Exception> errors, bool error)
		{
			List<string> lines = new List<string> (output.Split (new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries));

			// filter
			for (int i = 0; i < lines.Count; i++) {
				string line = lines [i];

				if (errors.Count > 100)
					return;

				if (line.Contains ("ld: warning: ignoring file ") && 
					line.Contains ("file was built for") && 
					line.Contains ("which is not the architecture being linked") &&
				// Only ignore warnings related to the object files we've built ourselves (assemblies, main.m, registrar.m)
					inputs.Any ((v) => line.Contains (v))) {
					continue;
				} else if (line.Contains ("ld: symbol(s) not found for architecture") && errors.Count > 0) {
					continue;
				} else if (line.Contains ("clang: error: linker command failed with exit code 1")) {
					continue;
				} else if (line.Contains ("was built for newer iOS version (5.1.1) than being linked (5.1)")) {
					continue;
				}

				if (line.Contains ("Undefined symbols for architecture")) {
					while (++i < lines.Count) {
						line = lines [i];
						if (!line.EndsWith (", referenced from:", StringComparison.Ordinal))
							break;

						var symbol = line.Replace (", referenced from:", "").Trim ('\"', ' ');
						if (symbol.StartsWith ("_OBJC_CLASS_$_", StringComparison.Ordinal)) {
							errors.Add (new MonoTouchException (5211, error, 
																"Native linking failed, undefined Objective-C class: {0}. The symbol '{1}' could not be found in any of the libraries or frameworks linked with your application.",
							                                    symbol.Replace ("_OBJC_CLASS_$_", ""), symbol));
						} else {
							var member = target.GetMemberForSymbol (symbol.Substring (1));
							if (member != null) {
								// Neither P/Invokes nor fields have IL, so we can't find the source code location.
								errors.Add (new MonoTouchException (5214, error,
									"Native linking failed, undefined symbol: {0}. " +
									"This symbol was referenced by the managed member {1}.{2}. " +
									"Please verify that all the necessary frameworks have been referenced and native libraries linked.",
									symbol, member.DeclaringType.FullName, member.Name));
							} else {
								errors.Add (new MonoTouchException (5210, error, 
							                                    "Native linking failed, undefined symbol: {0}. " +
																"Please verify that all the necessary frameworks have been referenced and native libraries are properly linked in.",
							                                    symbol));
							}
						}

						// skip all subsequent lines related to the same error.
						// we skip all subsequent lines with more indentation than the initial line.
						var indent = GetIndentation (line);
						while (i + 1 < lines.Count) {
							line = lines [i + 1];
							if (GetIndentation (lines [i + 1]) <= indent)
								break;
							i++;
						}
					}
				} else if (line.StartsWith ("duplicate symbol", StringComparison.Ordinal) && line.EndsWith (" in:", StringComparison.Ordinal)) {
					var symbol = line.Replace ("duplicate symbol ", "").Replace (" in:", "").Trim ();
					errors.Add (new MonoTouchException (5212, error, "Native linking failed, duplicate symbol: '{0}'.", symbol));

					var indent = GetIndentation (line);
					while (i + 1 < lines.Count) {
						line = lines [i + 1];
						if (GetIndentation (lines [i + 1]) <= indent)
							break;
						i++;
						errors.Add (new MonoTouchException (5213, error, "Duplicate symbol in: {0} (Location related to previous error)", line.Trim ()));
					}
				} else {
					if (line.StartsWith ("ld: ", StringComparison.Ordinal))
						line = line.Substring (4);

					line = line.Trim ();

					if (error) {
						errors.Add (new MonoTouchException (5209, error, "Native linking error: {0}", line));
					} else {
						errors.Add (new MonoTouchException (5203, error, "Native linking warning: {0}", line));
					}
				}
			}
		}

		static int GetIndentation (string line)
		{
			int rv = 0;
			if (line.Length == 0)
				return 0;

			while (true) {
				switch (line [rv]) {
				case ' ':
				case '\t':
					rv++;
					break;
				default:
					return rv;
				}
			};
		}

		// return the ids found in a macho file
		List<Guid> GetUuids (MachOFile file)
		{
			var result = new List<Guid> ();
			foreach (var cmd in file.load_commands) {
				if (cmd is UuidCommand) {
					var uuidCmd = cmd as UuidCommand;
					result.Add (new Guid (uuidCmd.uuid));
				}
			}
			return result;
		}

		// This method generates the manifest that is required by the symbolication in order to be able to debug the application, 
		// The following is an example of the manifest to be generated:
		// <mono-debug version=”1”>
		//	<app-id>com.foo.bar</app-id>
		//	<build-date>datetime</build-date>
		//	<build-id>build-id</build-id>
		//	<build-id>build-id</build-id>
		// </mono-debug>
		// where:
		// 
		// app-id: iOS/Android/Mac app/package ID. Currently for verification and user info only but in future may be used to find symbols automatically.
		// build-date: Local time in DateTime “O” format. For user info only.
		// build-id: The build UUID. Needed for HockeyApp to find the mSYM folder matching the app build. There may be more than one, as in the case of iOS multi-arch.
		void GenerateMSymManifest (Target target, string target_directory)
		{
			var manifestPath = Path.Combine (target_directory, "manifest.xml");
			if (String.IsNullOrEmpty (target_directory))
				throw new ArgumentNullException (nameof (target_directory));
			var root = new XElement ("mono-debug",
				new XAttribute("version", 1),
				new XElement ("app-id", BundleId),
				new XElement ("build-date", DateTime.Now.ToString ("O")));
				
			var file = MachO.Read (target.Executable);
			
			if (file is MachO) {
				var mfile = file as MachOFile;
				var uuids = GetUuids (mfile);
				foreach (var str in uuids) {
					root.Add (new XElement ("build-id", str));
				}
			} else if (file is IEnumerable<MachOFile>) {
				var ffile = file as IEnumerable<MachOFile>;
				foreach (var fentry in ffile) {
					var uuids = GetUuids (fentry);
					foreach (var str in uuids) {
						root.Add (new XElement ("build-id", str));
					}
				}
				
			} else {
				// do not write a manifest
				return;
			}

			// Write only if we need to update the manifest
			Driver.WriteIfDifferent (manifestPath, root.ToString ());
		}

		void CopyAotData (string src, string dest)
		{
			if (string.IsNullOrEmpty (src) || string.IsNullOrEmpty (dest)) {
				ErrorHelper.Warning (95, $"Aot files could not be copied to the destination directory {dest}"); 
				return;
			}
				
			var dir = new DirectoryInfo (src);
			if (!dir.Exists) {
				ErrorHelper.Warning (95, $"Aot files could not be copied to the destination directory {dest}"); 
				return;
			}

			var dirs = dir.GetDirectories ();
			if (!Directory.Exists (dest))
				Directory.CreateDirectory (dest);
				
			var files = dir.GetFiles ();
			foreach (var file in files) {
				var tmp = Path.Combine (dest, file.Name);
				file.CopyTo (tmp, true);
			}

			foreach (var subdir in dirs) {
				var tmp = Path.Combine (dest, subdir.Name);
				CopyAotData (subdir.FullName, tmp);
			}
		}

		public void BuildMSymDirectory ()
		{
			if (!EnableMSym)
				return;

			var target_directory = string.Format ("{0}.mSYM", AppDirectory);
			if (!Directory.Exists (target_directory))
				Directory.CreateDirectory (target_directory);

			foreach (var target in Targets) {
				GenerateMSymManifest (target, target_directory);
				var msymdir = Path.Combine (target.BuildDirectory, "Msym");
				// copy aot data must be done BEFORE we do copy the msym one
				CopyAotData (msymdir, target_directory);
				
				// copy all assemblies under mvid and with the dll and mdb
				var tmpdir =  Path.Combine (msymdir, "Msym", "tmp");
				if (!Directory.Exists (tmpdir))
					Directory.CreateDirectory (tmpdir);
					
				foreach (var asm in target.Assemblies) {
					asm.CopyToDirectory (tmpdir, reload: false, only_copy: true);
				}
				// mono-symbolicate knows best
				CopyMSymData (target_directory, tmpdir);
			}
		}

		public void BuildDsymDirectory ()
		{
			if (!BuildDSym.HasValue)
				BuildDSym = IsDeviceBuild;

			if (!BuildDSym.Value)
				return;

			string dsym_dir = string.Format ("{0}.dSYM", AppDirectory);
			bool cached_dsym = false;

			if (cached_executable)
				cached_dsym = IsUptodate (new string [] { Executable }, Directory.EnumerateFiles (dsym_dir, "*", SearchOption.AllDirectories));

			if (!cached_dsym) {
				if (Directory.Exists (dsym_dir))
					Directory.Delete (dsym_dir, true);
				
				Driver.CreateDsym (AppDirectory, ExecutableName, dsym_dir);
			} else {
				Driver.Log (3, "Target '{0}' is up-to-date.", dsym_dir);
			}
			Driver.Watch ("Linking DWARF symbols", 1);
		}

		IEnumerable<string> GetRequiredSymbols ()
		{
			foreach (var target in Targets) {
				foreach (var symbol in target.GetRequiredSymbols ())
					yield return symbol;
			}
		}

		bool WriteSymbolList (string filename)
		{
			var required_symbols = GetRequiredSymbols ().ToArray ();
			using (StreamWriter writer = new StreamWriter (filename)) {
				foreach (string symbol in required_symbols)
					writer.WriteLine ("_{0}", symbol);
				foreach (var symbol in NoSymbolStrip)
					writer.WriteLine ("_{0}", symbol);
				writer.Flush ();
				return writer.BaseStream.Position > 0;
			}
		}

		void StripNativeCode (string name)
		{
			if (NativeStrip && IsDeviceBuild && !EnableDebug && string.IsNullOrEmpty (SymbolList)) {
				string symbol_file = Path.Combine (Cache.Location, "symbol-file");
				if (WriteSymbolList (symbol_file)) {
					Driver.RunStrip (String.Format ("-i -s \"{0}\" \"{1}\"", symbol_file, Executable));
				} else {
					Driver.RunStrip (String.Format ("\"{0}\"", Executable));
				}
				Driver.Watch ("Native Strip", 1);
			}

			if (!string.IsNullOrEmpty (SymbolList))
				WriteSymbolList (SymbolList);
		}

		public void StripNativeCode ()
		{
			if (IsDualBuild) {
				bool cached = true;
				foreach (var target in Targets)
					cached &= target.CachedExecutable;
				if (!cached)
					StripNativeCode (Executable);
			} else {
				foreach (var target in Targets) {
					if (!target.CachedExecutable)
						StripNativeCode (target.Executable);
				}
			}
		}

		public void BundleAssemblies ()
		{
			var strip = ManagedStrip && IsDeviceBuild && !EnableDebug && !PackageMdb;

			var grouped = Targets.SelectMany ((Target t) => t.Assemblies).GroupBy ((Assembly asm) => asm.Identity);
			foreach (var @group in grouped) {
				var filename = @group.Key;
				var assemblies = @group.AsEnumerable ().ToArray ();
				var build_target = assemblies [0].BuildTarget;
				var size_specific = assemblies.Length > 1 && !Cache.CompareAssemblies (assemblies [0].FullPath, assemblies [1].FullPath, true, true);

				// Determine where to put the assembly
				switch (build_target) {
				case AssemblyBuildTarget.StaticObject:
				case AssemblyBuildTarget.DynamicLibrary:
					if (size_specific) {
						assemblies [0].CopyToDirectory (assemblies [0].Target.AppTargetDirectory, copy_mdb: PackageMdb, strip: strip, only_copy: true);
						assemblies [1].CopyToDirectory (assemblies [1].Target.AppTargetDirectory, copy_mdb: PackageMdb, strip: strip, only_copy: true);
					} else {
						assemblies [0].CopyToDirectory (AppDirectory, copy_mdb: PackageMdb, strip: strip, only_copy: true);
					}
					break;
				case AssemblyBuildTarget.Framework:
					// Put our resources in a subdirectory in the framework
					// But don't use 'Resources', because the app ends up being undeployable:
					// "PackageInspectionFailed: Failed to load Info.plist from bundle at path /private/var/installd/Library/Caches/com.apple.mobile.installd.staging/temp.CR0vmK/extracted/testapp.app/Frameworks/TestApp.framework"
					var target_name = assemblies [0].BuildTargetName;
					var resource_directory = Path.Combine (AppDirectory, "Frameworks", $"{target_name}.framework", "MonoBundle");
					if (size_specific) {
						assemblies [0].CopyToDirectory (Path.Combine (resource_directory, Path.GetFileName (assemblies [0].Target.AppTargetDirectory)), copy_mdb: PackageMdb, strip: strip, only_copy: true);
						assemblies [1].CopyToDirectory (Path.Combine (resource_directory, Path.GetFileName (assemblies [1].Target.AppTargetDirectory)), copy_mdb: PackageMdb, strip: strip, only_copy: true);
					} else {
						assemblies [0].CopyToDirectory (resource_directory, copy_mdb: PackageMdb, strip: strip, only_copy: true);
					}
					break;
				default:
					throw ErrorHelper.CreateError (100, "Invalid assembly build target: '{0}'. Please file a bug report with a test case (http://bugzilla.xamarin.com).", build_target);
				}
			}
		}

		public void GenerateRuntimeOptions ()
		{
			// only if the linker is disabled
			if (LinkMode != LinkMode.None)
				return;

			RuntimeOptions.Write (AppDirectory);
		}

		public void ProcessFrameworksForArguments (StringBuilder args, IEnumerable<string> frameworks, IEnumerable<string> weak_frameworks, IList<string> inputs)
		{
			bool any_user_framework = false;

			if (frameworks != null) {
				foreach (var fw in frameworks)
					ProcessFrameworkForArguments (args, fw, false, inputs, ref any_user_framework);
			}

			if (weak_frameworks != null) {
				foreach (var fw in weak_frameworks)
					ProcessFrameworkForArguments (args, fw, true, inputs, ref any_user_framework);
			}
			
			if (any_user_framework) {
				args.Append (" -Xlinker -rpath -Xlinker @executable_path/Frameworks");
				if (IsExtension)
					args.Append (" -Xlinker -rpath -Xlinker @executable_path/../../Frameworks");
			}

		}

		public static void ProcessFrameworkForArguments (StringBuilder args, string fw, bool is_weak, IList<string> inputs, ref bool any_user_framework)
		{
			var name = Path.GetFileNameWithoutExtension (fw);
			if (fw.EndsWith (".framework", StringComparison.Ordinal)) {
				// user framework, we need to pass -F to the linker so that the linker finds the user framework.
				any_user_framework = true;
				if (inputs != null)
					inputs.Add (Path.Combine (fw, name));
				args.Append (" -F ").Append (Driver.Quote (Path.GetDirectoryName (fw)));
			}
			args.Append (is_weak ? " -weak_framework " : " -framework ").Append (Driver.Quote (name));
		}

		public void CreateFrameworkInfoPList (string output_path, string framework_name, string bundle_identifier, string bundle_name)
		{
			var sb = new StringBuilder ();
			sb.AppendLine ("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
			sb.AppendLine ("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
			sb.AppendLine ("<plist version=\"1.0\">");
			sb.AppendLine ("<dict>");
			sb.AppendLine ("        <key>CFBundleDevelopmentRegion</key>");
			sb.AppendLine ("        <string>en</string>");
			sb.AppendLine ("        <key>CFBundleIdentifier</key>");
			sb.AppendLine ($"        <string>{bundle_identifier}</string>");
			sb.AppendLine ("        <key>CFBundleInfoDictionaryVersion</key>");
			sb.AppendLine ("        <string>6.0</string>");
			sb.AppendLine ("        <key>CFBundleName</key>");
			sb.AppendLine ($"        <string>{bundle_name}</string>");
			sb.AppendLine ("        <key>CFBundlePackageType</key>");
			sb.AppendLine ("        <string>FMWK</string>");
			sb.AppendLine ("        <key>CFBundleShortVersionString</key>");
			sb.AppendLine ("        <string>1.0</string>");
			sb.AppendLine ("        <key>CFBundleSignature</key>");
			sb.AppendLine ("        <string>????</string>");
			sb.AppendLine ("        <key>CFBundleVersion</key>");
			sb.AppendLine ("        <string>1.0</string>");
			sb.AppendLine ("        <key>NSPrincipalClass</key>");
			sb.AppendLine ("        <string></string>");
			sb.AppendLine ("        <key>CFBundleExecutable</key>");
			sb.AppendLine ($"        <string>{framework_name}</string>");
			sb.AppendLine ("        <key>BuildMachineOSBuild</key>");
			sb.AppendLine ("        <string>13F34</string>");
			sb.AppendLine ("        <key>CFBundleSupportedPlatforms</key>");
			sb.AppendLine ("        <array>");
			sb.AppendLine ($"                <string>{Driver.GetPlatform (this)}</string>");
			sb.AppendLine ("        </array>");
			sb.AppendLine ("        <key>DTCompiler</key>");
			sb.AppendLine ("        <string>com.apple.compilers.llvm.clang.1_0</string>");
			sb.AppendLine ("        <key>DTPlatformBuild</key>");
			sb.AppendLine ("        <string>12D508</string>");
			sb.AppendLine ("        <key>DTPlatformName</key>");
			sb.AppendLine ($"        <string>{Driver.GetPlatform (this).ToLowerInvariant ()}</string>");
			sb.AppendLine ("        <key>DTPlatformVersion</key>");
			sb.AppendLine ($"        <string>{SdkVersions.GetVersion (Platform)}</string>");
			sb.AppendLine ("        <key>DTSDKBuild</key>");
			sb.AppendLine ("        <string>12D508</string>");
			sb.AppendLine ("        <key>DTSDKName</key>");
			sb.AppendLine ($"        <string>{Driver.GetPlatform (this)}{SdkVersion}</string>");
			sb.AppendLine ("        <key>DTXcode</key>");
			sb.AppendLine ("        <string>0620</string>");
			sb.AppendLine ("        <key>DTXcodeBuild</key>");
			sb.AppendLine ("        <string>6C131e</string>");
			sb.AppendLine ("        <key>MinimumOSVersion</key>");
			sb.AppendLine ($"        <string>{DeploymentTarget.ToString ()}</string>");
			sb.AppendLine ("        <key>UIDeviceFamily</key>");
			sb.AppendLine ("        <array>");
			switch (Platform) {
			case ApplePlatform.iOS:
				sb.AppendLine ("                <integer>1</integer>");
				sb.AppendLine ("                <integer>2</integer>");
				break;
			case ApplePlatform.TVOS:
				sb.AppendLine ("                <integer>3</integer>");
				break;
			case ApplePlatform.WatchOS:
				sb.AppendLine ("                <integer>4</integer>");
				break;
			default:
				throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", Platform);
			}
			sb.AppendLine ("        </array>");

			sb.AppendLine ("</dict>");
			sb.AppendLine ("</plist>");

			Driver.WriteIfDifferent (output_path, sb.ToString ());
		}
	}
}
