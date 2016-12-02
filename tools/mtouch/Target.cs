﻿// Copyright 2013--2014 Xamarin Inc. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

using MonoTouch.Tuner;

using Mono.Cecil;
using Mono.Tuner;
using Mono.Linker;
using Xamarin.Linker;

using Xamarin.Utils;

using XamCore.Registrar;

namespace Xamarin.Bundler
{
	public class BundleFileInfo
	{
		public HashSet<string> Sources = new HashSet<string> ();
		public bool DylibToFramework;
	}

	public partial class Target {
		public string TargetDirectory;
		public string AppTargetDirectory;

		public MonoTouchManifestResolver ManifestResolver = new MonoTouchManifestResolver ();
		public AssemblyDefinition ProductAssembly;

		// directories used during the build process
		public string ArchDirectory;
		public string PreBuildDirectory;
		public string BuildDirectory;
		public string LinkDirectory;

		// Note that each 'Target' can have multiple abis: armv7+armv7s for instance.
		public List<Abi> Abis;

		public Dictionary<string, BundleFileInfo> BundleFiles = new Dictionary<string, BundleFileInfo> ();

		Dictionary<Abi, CompileTask> pinvoke_tasks = new Dictionary<Abi, CompileTask> ();
		List<CompileTask> link_with_task_output = new List<CompileTask> ();
		CompilerFlags linker_flags;

		// If we didn't link because the existing (cached) assemblyes are up-to-date.
		bool cached_link;

		// If any assemblies were updated (only set to false if the linker is disabled and no assemblies were modified).
		bool any_assembly_updated = true;

		//BuildTasks compile_tasks = new BuildTasks ();

		// If we didn't link the final executable because the existing binary is up-to-date.
		public bool cached_executable; 

		// If the assemblies were symlinked.
		public bool Symlinked;

		public bool Is32Build { get { return Application.IsArchEnabled (Abis, Abi.Arch32Mask); } } // If we're targetting a 32 bit arch for this target.
		public bool Is64Build { get { return Application.IsArchEnabled (Abis, Abi.Arch64Mask); } } // If we're targetting a 64 bit arch for this target.

		public void AddToBundle (string source, string bundle_path = null, bool dylib_to_framework_conversion = false)
		{
			BundleFileInfo info;

			if (bundle_path == null) {
				if (source.EndsWith (".framework", StringComparison.Ordinal)) {
					var bundle_name = Path.GetFileNameWithoutExtension (source);
					bundle_path = $"Frameworks/{bundle_name}.framework";
				} else {
					bundle_path = Path.GetFileName (source);
				}
			}

			if (!BundleFiles.TryGetValue (bundle_path, out info))
				BundleFiles [bundle_path] = info = new BundleFileInfo () { DylibToFramework = dylib_to_framework_conversion };
			if (info.DylibToFramework != dylib_to_framework_conversion)
				throw new Exception (); // internal error.
			info.Sources.Add (source);
		}

		public void LinkWithTaskOutput (CompileTask task)
		{
			if (task.SharedLibrary) {
				LinkWithDynamicLibrary (task.OutputFile);
			} else {
				LinkWithStaticLibrary (task.OutputFile);
			}
			link_with_task_output.Add (task);
		}

		public void LinkWithTaskOutput (IEnumerable<CompileTask> tasks)
		{
			foreach (var t in tasks)
				LinkWithTaskOutput (t);
		}

		public void LinkWithStaticLibrary (string path)
		{
			linker_flags.AddLinkWith (path);
		}

		public void LinkWithStaticLibrary (IEnumerable<string> paths)
		{
			linker_flags.AddLinkWith (paths);
		}

		public void LinkWithFramework (string path)
		{
			linker_flags.AddFramework (path);
		}

		public void LinkWithDynamicLibrary (string path)
		{
			linker_flags.AddLinkWith (path);
		}

		PInvokeWrapperGenerator pinvoke_state;
		PInvokeWrapperGenerator MarshalNativeExceptionsState {
			get {
				if (!App.RequiresPInvokeWrappers)
					return null;

				if (pinvoke_state == null) {
					pinvoke_state = new PInvokeWrapperGenerator ()
					{
						SourcePath = Path.Combine (ArchDirectory, "pinvokes.m"),
						HeaderPath = Path.Combine (ArchDirectory, "pinvokes.h"),
						Registrar = (StaticRegistrar) StaticRegistrar,
					};
				}

				return pinvoke_state;
			}
		}

		public string Executable {
			get {
				return Path.Combine (TargetDirectory, App.ExecutableName);
			}
		}

		public void Initialize (bool show_warnings)
		{
			// we want to load our own mscorlib[-runtime].dll, not something else we're being feeded
			// (e.g. bug #6612) since it might not match the libmono[-sgen].a library we'll link with,
			// so load the corlib we want first.

			var corlib_path = Path.Combine (Resolver.FrameworkDirectory, "mscorlib.dll");
			var corlib = ManifestResolver.Load (corlib_path);
			if (corlib == null)
				throw new MonoTouchException (2006, true, "Can not load mscorlib.dll from: '{0}'. Please reinstall Xamarin.iOS.", corlib_path);

			foreach (var reference in App.References) {
				var ad = ManifestResolver.Load (reference);
				if (ad == null)
					throw new MonoTouchException (2002, true, "Can not resolve reference: {0}", reference);
				if (ad.MainModule.Runtime > TargetRuntime.Net_4_0)
					ErrorHelper.Show (new MonoTouchException (11, false, "{0} was built against a more recent runtime ({1}) than Xamarin.iOS supports.", Path.GetFileName (reference), ad.MainModule.Runtime));

				// Figure out if we're referencing Xamarin.iOS or monotouch.dll
				if (Path.GetFileNameWithoutExtension (ad.MainModule.FileName) == Driver.ProductAssembly)
					ProductAssembly = ad;
			}

			ComputeListOfAssemblies ();

			if (App.LinkMode == LinkMode.None && App.I18n != I18nAssemblies.None)
				AddI18nAssemblies ();

			linker_flags = new CompilerFlags (this);

			// an extension is a .dll and it would match itself
			if (!App.IsExtension) {
				var root_wo_ext = Path.GetFileNameWithoutExtension (App.RootAssembly);
				foreach (var assembly in Assemblies) {
					if (!assembly.FullPath.EndsWith (".exe", StringComparison.OrdinalIgnoreCase)) {
						if (root_wo_ext == Path.GetFileNameWithoutExtension (assembly.FullPath))
							throw new MonoTouchException (23, true, "Application name '{0}.exe' conflicts with another user assembly.", root_wo_ext);
					}
				}
			}
		}

		// This is to load the symbols for all assemblies, so that we can give better error messages
		// (with file name / line number information).
		public void LoadSymbols ()
		{
			foreach (var a in Assemblies)
				a.LoadSymbols ();
		}

		IEnumerable<AssemblyDefinition> GetAssemblies ()
		{
			if (App.LinkMode == LinkMode.None)
				return ManifestResolver.GetAssemblies ();

			List<AssemblyDefinition> assemblies = new List<AssemblyDefinition> ();
			if (LinkContext == null) {
				// use data from cache
				foreach (var assembly in Assemblies)
					assemblies.Add (assembly.AssemblyDefinition);
			} else {
				foreach (var assembly in LinkContext.GetAssemblies ()) {
					if (LinkContext.Annotations.GetAction (assembly) == AssemblyAction.Delete)
						continue;

					assemblies.Add (assembly);
				}
			}
			return assemblies;
		}

		public void ComputeLinkerFlags ()
		{
			foreach (var a in Assemblies)
				a.ComputeLinkerFlags ();

			if (App.Platform != ApplePlatform.WatchOS && App.Platform != ApplePlatform.TVOS)
				Frameworks.Add ("CFNetwork"); // required by xamarin_start_wwan
		}

		Dictionary<string, MemberReference> entry_points;
		public IDictionary<string, MemberReference> GetEntryPoints ()
		{
			if (entry_points == null)
				GetRequiredSymbols ();
			return entry_points;
		}

		public IEnumerable<string> GetRequiredSymbols ()
		{
			if (entry_points != null)  
				return entry_points.Keys;

			var cache_location = Path.Combine (Cache.Location, "entry-points.txt");
			if (cached_link || !any_assembly_updated) {
				entry_points = new Dictionary<string, MemberReference> ();
				foreach (var ep in File.ReadAllLines (cache_location))
					entry_points.Add (ep, null);
			} else {
				List<MethodDefinition> marshal_exception_pinvokes;
				if (LinkContext == null) {
					// This happens when using the simlauncher and the msbuild tasks asked for a list
					// of symbols (--symbollist). In that case just produce an empty list, since the
					// binary shouldn't end up stripped anyway.
					entry_points = new Dictionary<string, MemberReference> ();
					marshal_exception_pinvokes = new List<MethodDefinition> ();
				} else {
					entry_points = LinkContext.RequiredSymbols;
					marshal_exception_pinvokes = LinkContext.MarshalExceptionPInvokes;
				}
				
				// keep the debugging helper in debugging binaries only
				if (App.EnableDebug && !App.EnableBitCode)
					entry_points.Add ("mono_pmip", null);

				if (App.IsSimulatorBuild) {
					entry_points.Add ("xamarin_dyn_objc_msgSend", null);
					entry_points.Add ("xamarin_dyn_objc_msgSendSuper", null);
					entry_points.Add ("xamarin_dyn_objc_msgSend_stret", null);
					entry_points.Add ("xamarin_dyn_objc_msgSendSuper_stret", null);
				}

				File.WriteAllText (cache_location, string.Join ("\n", entry_points.Keys.ToArray ()));
			}
			return entry_points.Keys;
		}

		public MemberReference GetMemberForSymbol (string symbol)
		{
			MemberReference rv = null;
			entry_points?.TryGetValue (symbol, out rv);
			return rv;
		}

		//
		// Gets a flattened list of all the assemblies pulled by the root assembly
		//
		public void ComputeListOfAssemblies ()
		{
			var exceptions = new List<Exception> ();
			var assemblies = new HashSet<string> ();

			try {
				var assembly = ManifestResolver.Load (App.RootAssembly);
				ComputeListOfAssemblies (assemblies, assembly, exceptions);
			} catch (MonoTouchException mte) {
				exceptions.Add (mte);
			} catch (Exception e) {
				exceptions.Add (new MonoTouchException (9, true, e, "Error while loading assemblies: {0}", e.Message));
			}

			if (App.LinkMode == LinkMode.None)
				exceptions.AddRange (ManifestResolver.list);

			if (exceptions.Count > 0)
				throw new AggregateException (exceptions);
		}

		void ComputeListOfAssemblies (HashSet<string> assemblies, AssemblyDefinition assembly, List<Exception> exceptions)
		{
			if (assembly == null)
				return;

			var fqname = assembly.MainModule.FileName;
			if (assemblies.Contains (fqname))
				return;

			assemblies.Add (fqname);

			var asm = new Assembly (this, assembly);
			asm.ComputeSatellites ();
			this.Assemblies.Add (asm);

			var main = assembly.MainModule;
			foreach (AssemblyNameReference reference in main.AssemblyReferences) {
				// Verify that none of the references references an incorrect platform assembly.
				switch (reference.Name) {
				case "monotouch":
				case "Xamarin.iOS":
				case "Xamarin.TVOS":
				case "Xamarin.WatchOS":
					if (reference.Name != Driver.ProductAssembly)
						exceptions.Add (ErrorHelper.CreateError (34, "Cannot reference '{0}.dll' in a {1} project - it is implicitly referenced by '{2}'.", reference.Name, Driver.TargetFramework.Identifier, assembly.FullName));
					break;
				}

				var reference_assembly = ManifestResolver.Resolve (reference);
				ComputeListOfAssemblies (assemblies, reference_assembly, exceptions);
			}

			// Custom Attribute metadata can include references to other assemblies, e.g. [X (typeof (Y)], 
			// but it is not reflected in AssemblyReferences :-( ref: #37611
			// so we must scan every custom attribute to look for System.Type
			GetCustomAttributeReferences (assembly, assemblies, exceptions);
			GetCustomAttributeReferences (main, assemblies, exceptions);
			if (main.HasTypes) {
				foreach (var t in main.Types) {
					GetTypeReferences (t, assemblies, exceptions);
				}
			}
		}

		void GetTypeReferences (TypeDefinition type, HashSet<string> assemblies, List<Exception> exceptions)
		{
			GetCustomAttributeReferences (type, assemblies, exceptions);
			if (type.HasEvents) {
				foreach (var e in type.Events)
					GetCustomAttributeReferences (e, assemblies, exceptions);
			}
			if (type.HasFields) {
				foreach (var f in type.Fields)
					GetCustomAttributeReferences (f, assemblies, exceptions);
			}
			if (type.HasMethods) {
				foreach (var m in type.Methods)
					GetCustomAttributeReferences (m, assemblies, exceptions);
			}
			if (type.HasProperties) {
				foreach (var p in type.Properties)
					GetCustomAttributeReferences (p, assemblies, exceptions);
			}
			if (type.HasNestedTypes) {
				foreach (var nt in type.NestedTypes)
					GetTypeReferences (nt, assemblies, exceptions);
			}
		}

		void GetCustomAttributeReferences (ICustomAttributeProvider cap, HashSet<string> assemblies, List<Exception> exceptions)
		{
			if (!cap.HasCustomAttributes)
				return;
			foreach (var ca in cap.CustomAttributes) {
				if (ca.HasConstructorArguments) {
					foreach (var arg in ca.ConstructorArguments)
						GetCustomAttributeArgumentReference (arg, assemblies, exceptions);
				}
				if (ca.HasFields) {
					foreach (var arg in ca.Fields)
						GetCustomAttributeArgumentReference (arg.Argument, assemblies, exceptions);
				}
				if (ca.HasProperties) {
					foreach (var arg in ca.Properties)
						GetCustomAttributeArgumentReference (arg.Argument, assemblies, exceptions);
				}
			}
		}

		void GetCustomAttributeArgumentReference (CustomAttributeArgument arg, HashSet<string> assemblies, List<Exception> exceptions)
		{
			if (!arg.Type.Is ("System", "Type"))
				return;
			var ar = (arg.Value as TypeReference)?.Scope as AssemblyNameReference;
			if (ar == null)
				return;
			var reference_assembly = ManifestResolver.Resolve (ar);
			ComputeListOfAssemblies (assemblies, reference_assembly, exceptions);
		}

		bool IncludeI18nAssembly (Mono.Linker.I18nAssemblies assembly)
		{
			return (App.I18n & assembly) != 0;
		}

		public void AddI18nAssemblies ()
		{
			Assemblies.Add (LoadI18nAssembly ("I18N"));

			if (IncludeI18nAssembly (Mono.Linker.I18nAssemblies.CJK))
				Assemblies.Add (LoadI18nAssembly ("I18N.CJK"));

			if (IncludeI18nAssembly (Mono.Linker.I18nAssemblies.MidEast))
				Assemblies.Add (LoadI18nAssembly ("I18N.MidEast"));

			if (IncludeI18nAssembly (Mono.Linker.I18nAssemblies.Other))
				Assemblies.Add (LoadI18nAssembly ("I18N.Other"));

			if (IncludeI18nAssembly (Mono.Linker.I18nAssemblies.Rare))
				Assemblies.Add (LoadI18nAssembly ("I18N.Rare"));

			if (IncludeI18nAssembly (Mono.Linker.I18nAssemblies.West))
				Assemblies.Add (LoadI18nAssembly ("I18N.West"));
		}

		Assembly LoadI18nAssembly (string name)
		{
			var assembly = ManifestResolver.Resolve (AssemblyNameReference.Parse (name));
			return new Assembly (this, assembly);
		}

		public void LinkAssemblies (string main, ref List<string> assemblies, string output_dir, out MonoTouchLinkContext link_context)
		{
			if (Driver.Verbosity > 0)
				Console.WriteLine ("Linking {0} into {1} using mode '{2}'", main, output_dir, App.LinkMode);

			var cache = Resolver.ToResolverCache ();
			var resolver = cache != null
				? new AssemblyResolver (cache)
				: new AssemblyResolver ();

			resolver.AddSearchDirectory (Resolver.RootDirectory);
			resolver.AddSearchDirectory (Resolver.FrameworkDirectory);

			LinkerOptions = new LinkerOptions {
				MainAssembly = Resolver.Load (main),
				OutputDirectory = output_dir,
				LinkMode = App.LinkMode,
				Resolver = resolver,
				SkippedAssemblies = App.LinkSkipped,
				I18nAssemblies = App.I18n,
				LinkSymbols = true,
				LinkAway = App.LinkAway,
				ExtraDefinitions = App.Definitions,
				Device = App.IsDeviceBuild,
				// by default we keep the code to ensure we're executing on the UI thread (for UI code) for debug builds
				// but this can be overridden to either (a) remove it from debug builds or (b) keep it in release builds
				EnsureUIThread = App.ThreadCheck.HasValue ? App.ThreadCheck.Value : App.EnableDebug,
				DebugBuild = App.EnableDebug,
				Arch = Is64Build ? 8 : 4,
				IsDualBuild = App.IsDualBuild,
				DumpDependencies = App.LinkerDumpDependencies,
				RuntimeOptions = App.RuntimeOptions,
				MarshalNativeExceptionsState = MarshalNativeExceptionsState,
			};

			MonoTouch.Tuner.Linker.Process (LinkerOptions, out link_context, out assemblies);

			Driver.Watch ("Link Assemblies", 1);
		}

		public void ManagedLink ()
		{
			var cache_path = Path.Combine (ArchDirectory, "linked-assemblies.txt");

			foreach (var a in Assemblies)
				a.CopyToDirectory (LinkDirectory, false, check_case: true);

			// Check if we can use a previous link result.
			if (!Driver.Force) {
				var input = new List<string> ();
				var output = new List<string> ();
				var cached_output = new List<string> ();

				if (File.Exists (cache_path)) {
					cached_output.AddRange (File.ReadAllLines (cache_path));

					var cached_loaded = new HashSet<string> ();
					// Only add the previously linked assemblies (and their satellites) as the input/output assemblies.
					// Do not add assemblies which the linker process removed.
					foreach (var a in Assemblies) {
						if (!cached_output.Contains (a.FullPath))
							continue;
						cached_loaded.Add (a.FullPath);
						input.Add (a.FullPath);
						output.Add (Path.Combine (PreBuildDirectory, a.FileName));
						if (File.Exists (a.FullPath + ".mdb")) {
							// Debug files can change without the assemblies themselves changing
							// This should also invalidate the cached linker results, since the non-linked mdbs can't be copied.
							input.Add (a.FullPath + ".mdb");
							output.Add (Path.Combine (PreBuildDirectory, a.FileName) + ".mdb");
						}
						
						if (a.Satellites != null) {
							foreach (var s in a.Satellites) {
								input.Add (s);
								output.Add (Path.Combine (PreBuildDirectory, Path.GetFileName (Path.GetDirectoryName (s)), Path.GetFileName (s)));
								// No need to copy satellite mdb files, satellites are resource-only assemblies.
							}
						}
					}

					// The linker might have added assemblies that weren't specified/reachable
					// from the command line arguments (such as I18N assemblies). Those are not
					// in the Assemblies list at this point (since we haven't run the linker yet)
					// so make sure we take those into account as well.
					var not_loaded = cached_output.Except (cached_loaded);
					foreach (var path in not_loaded) {
						input.Add (path);
						output.Add (Path.Combine (PreBuildDirectory, Path.GetFileName (path)));
					}

					// Include mtouch here too?
					// input.Add (Path.Combine (MTouch.MonoTouchDirectory, "usr", "bin", "mtouch"));

					if (Application.IsUptodate (input, output)) {
						cached_link = true;
						for (int i = Assemblies.Count - 1; i >= 0; i--) {
							var a = Assemblies [i];
							if (!cached_output.Contains (a.FullPath)) {
								Assemblies.RemoveAt (i);
								continue;
							}
							// Load the cached assembly
							a.LoadAssembly (Path.Combine (PreBuildDirectory, a.FileName));
							Driver.Log (3, "Target '{0}' is up-to-date.", a.FullPath);
						}

						foreach (var path in not_loaded) {
							var a = new Assembly (this, path);
							a.LoadAssembly (Path.Combine (PreBuildDirectory, a.FileName));
							Assemblies.Add (a);
						}

						Driver.Watch ("Cached assemblies reloaded", 1);
						Driver.Log ("Cached assemblies reloaded.");

						return;
					}
				}
			}

			// Load the assemblies into memory.
			foreach (var a in Assemblies)
				a.LoadAssembly (a.FullPath);

			var assemblies = new List<string> ();
			foreach (var a in Assemblies)
				assemblies.Add (a.FullPath);
			var linked_assemblies = new List<string> (assemblies);

			LinkAssemblies (App.RootAssembly, ref linked_assemblies, PreBuildDirectory, out LinkContext);

			// Remove assemblies that were linked away
			var removed = new HashSet<string> (assemblies);
			removed.ExceptWith (linked_assemblies);

			foreach (var assembly in removed) {
				for (int i = Assemblies.Count - 1; i >= 0; i--) {
					var ad = Assemblies [i];
					if (assembly != ad.FullPath)
						continue;

					Assemblies.RemoveAt (i);
				}
			}

			// anything added by the linker will have it's original path
			var added = new HashSet<string> ();
			foreach (var assembly in linked_assemblies)
				added.Add (Path.GetFileName (assembly));
			var original = new HashSet<string> ();
			foreach (var assembly in assemblies)
				original.Add (Path.GetFileName (assembly));
			added.ExceptWith (original);

			foreach (var assembly in added) {
				// the linker already copied the assemblies (linked or not) into the output directory
				// and we must NOT overwrite the linker work with an original (unlinked) assembly
				string path = Path.Combine (PreBuildDirectory, assembly);
				var ad = ManifestResolver.Load (path);
				var a = new Assembly (this, ad);
				a.CopyToDirectory (PreBuildDirectory);
				Assemblies.Add (a);
			}

			assemblies = linked_assemblies;

			// Make the assemblies point to the right path.
			foreach (var a in Assemblies)
				a.FullPath = Path.Combine (PreBuildDirectory, a.FileName);

			File.WriteAllText (cache_path, string.Join ("\n", linked_assemblies));
		}
			
		public void ProcessAssemblies ()
		{
			//
			// * Linking
			//   Copy assemblies to LinkDirectory
			//   Link and save to PreBuildDirectory
			//   If marshalling native exceptions:
			//     * Generate/calculate P/Invoke wrappers and save to PreBuildDirectory
			//   [AOT assemblies in BuildDirectory]
			//   Strip managed code save to TargetDirectory (or just copy the file if stripping is disabled).
			//
			// * No linking
			//   If marshalling native exceptions:
			//     Generate/calculate P/Invoke wrappers and save to PreBuildDirectory.
			//   If not marshalling native exceptions:
			//     Copy assemblies to PreBuildDirectory
			//     Copy unmodified assemblies to BuildDirectory
			//   [AOT assemblies in BuildDirectory]
			//   Strip managed code save to TargetDirectory (or just copy the file if stripping is disabled).
			//
			// Note that we end up copying assemblies around quite much,
			// this is because we we're comparing contents instead of 
			// filestamps, so we need the previous file around to be
			// able to do the actual comparison. For instance: in the
			// 'No linking' case above, we copy the assembly to PreBuild
			// before removing the resources and saving that result to Build.
			// The copy in PreBuild is required for the next build iteration,
			// to see if the original assembly has been modified or not (the
			// file in the Build directory might be different due to resource
			// removal even if the original assembly didn't change).
			//
			// This can probably be improved by storing digests/hashes instead
			// of the entire files, but this turned out a bit messy when
			// trying to make it work with the linker, so I decided to go for
			// simple file copying for now.
			//

			// 
			// Other notes:
			//
			// * We need all assemblies in the same directory when doing AOT-compilation.
			// * We cannot overwrite in-place, because it will mess up dependency tracking 
			//   and besides if we overwrite in place we might not be able to ignore
			//   insignificant changes (such as only a GUID change - the code is identical,
			//   but we still need the original assembly since the AOT-ed image also stores
			//   the GUID, and we fail at runtime if the GUIDs in the assembly and the AOT-ed
			//   image don't match - if we overwrite in-place we lose the original assembly and
			//   its GUID).
			// 

			LinkDirectory = Path.Combine (ArchDirectory, "Link");
			if (!Directory.Exists (LinkDirectory))
				Directory.CreateDirectory (LinkDirectory);

			PreBuildDirectory = Path.Combine (ArchDirectory, "PreBuild");
			if (!Directory.Exists (PreBuildDirectory))
				Directory.CreateDirectory (PreBuildDirectory);
			
			BuildDirectory = Path.Combine (ArchDirectory, "Build");
			if (!Directory.Exists (BuildDirectory))
				Directory.CreateDirectory (BuildDirectory);

			if (!Directory.Exists (TargetDirectory))
				Directory.CreateDirectory (TargetDirectory);

			ManagedLink ();

			// Now the assemblies are in PreBuildDirectory.

			foreach (var a in Assemblies) {
				var target = Path.Combine (BuildDirectory, a.FileName);
				if (!a.CopyAssembly (a.FullPath, target))
					Driver.Log (3, "Target '{0}' is up-to-date.", target);
				a.FullPath = target;
			}

			Driver.GatherFrameworks (this, Frameworks, WeakFrameworks);

			// Make sure there are no duplicates between frameworks and weak frameworks.
			// Keep the weak ones.
			Frameworks.ExceptWith (WeakFrameworks);
		}

		public void CompilePInvokeWrappers ()
		{
			if (!App.RequiresPInvokeWrappers)
				return;
		
			// Write P/Invokes
			var state = MarshalNativeExceptionsState;
			if (state.Started) {
				// The generator is 'started' by the linker, which means it may not
				// be started if the linker was not executed due to re-using cached results.
				state.End ();
			}

			var ifile = state.SourcePath;
			foreach (var abi in Abis) {
				var arch = abi.AsArchString ();
				string ofile;

				var mode = App.LibPInvokesLinkMode;
				switch (mode) {
				case AssemblyBuildTarget.StaticObject:
					ofile = Path.Combine (Cache.Location, arch, "libpinvokes.a");
					break;
				case AssemblyBuildTarget.DynamicLibrary:
					ofile = Path.Combine (Cache.Location, arch, "libpinvokes.dylib");
					break;
				case AssemblyBuildTarget.Framework:
					ofile = Path.Combine (Cache.Location, arch, "Xamarin.PInvokes.framework", "Xamarin.PInvokes");

					var plist_path = Path.Combine (Path.GetDirectoryName (ofile), "Info.plist");
					var fw_name = Path.GetFileNameWithoutExtension (ofile);
					App.CreateFrameworkInfoPList (plist_path, fw_name, Driver.App.BundleId + ".frameworks." + fw_name, fw_name);
					break;
				default:
					throw new Exception ();
				}

				var pinvoke_task = new PinvokesTask ()
				{
					Target = this,
					Abi = abi,
					InputFile = ifile,
					OutputFile = ofile,
					SharedLibrary = mode != AssemblyBuildTarget.StaticObject,
					Language = "objective-c++",
				};
				if (pinvoke_task.SharedLibrary) {
					if (mode == AssemblyBuildTarget.Framework) {
						var name = Path.GetFileNameWithoutExtension (ifile);
						pinvoke_task.InstallName = $"@rpath/{name}.framework/{name}";
						AddToBundle (pinvoke_task.OutputFile, $"Frameworks/${name}.framework/{name}", dylib_to_framework_conversion: true);
					} else {
						pinvoke_task.InstallName = $"@executable_path/{Path.GetFileName (ofile)}";
						AddToBundle (pinvoke_task.OutputFile);
					}
					pinvoke_task.CompilerFlags.AddFramework ("Foundation");
					pinvoke_task.CompilerFlags.LinkWithXamarin ();
				}

				pinvoke_tasks.Add (abi, pinvoke_task);

				LinkWithTaskOutput (pinvoke_task);
			}
		}

		public void SelectStaticRegistrar ()
		{
			switch (App.Registrar) {
			case RegistrarMode.Static:
			case RegistrarMode.Dynamic:
			case RegistrarMode.Default:
				StaticRegistrar = new StaticRegistrar (this)
				{
					LinkContext = LinkContext,
				};
				break;
			}
		}

		void AOTCompile ()
		{
			if (App.IsSimulatorBuild)
				return;

			foreach (var a in Assemblies) {
				foreach (var abi in Abis) {
					a.CreateAOTTask (abi);
				}
			}

			// Group the assemblies according to their target name, and build them all.
			var grouped = Assemblies.GroupBy ((arg) => arg.BuildTargetName);
			foreach (var abi in Abis) {
				foreach (var @group in grouped) {
					var name = @group.Key;
					var assemblies = @group.AsEnumerable ().ToArray ();

					Driver.Log (5, "Building {0} from {1}", name, string.Join (", ", assemblies.Select ((arg1) => Path.GetFileNameWithoutExtension (arg1.FileName)).ToArray ()));

					// We ensure elsewhere that all assemblies in a group have the same build target.
					var build_target = assemblies [0].BuildTarget;
					string install_name;
					string compiler_output;
					var compiler_flags = new CompilerFlags (this);
					var link_dependencies = new List<CompileTask> ();
					var infos = assemblies.Select ((asm) => asm.AotInfos [abi]);
					var aottasks = infos.Select ((info) => info.Task);

					// We have to compile any source files to object files before we can link.
					var sources = infos.SelectMany ((info) => info.AsmFiles);
					if (sources.Count () > 0) {
						foreach (var src in sources) {
							// We might have to convert .s to bitcode assembly (.ll) first
							var assembly = src;
							BitCodeifyTask bitcode_task = null;
							if (App.EnableAsmOnlyBitCode) {
								bitcode_task = new BitCodeifyTask ()
								{
									Input = assembly,
									OutputFile = Path.ChangeExtension (assembly, ".ll"),
									Platform = App.Platform,
									Abi = abi,
									DeploymentTarget = App.DeploymentTarget,
									Dependencies = aottasks,
								};
								assembly = bitcode_task.OutputFile;
							}

							// Compile assembly code (either .s or .ll) to object file
							var compile_task = new CompileTask
							{
								Target = this,
								SharedLibrary = false,
								InputFile = assembly,
								OutputFile = Path.ChangeExtension (assembly, ".o"),
								Abi = abi,
								Language = bitcode_task != null ? null : "assembler",
								Dependency = bitcode_task,
								Dependencies = aottasks,
							};
							link_dependencies.Add (compile_task);
						}
					}

					var arch = abi.AsArchString ();
					switch (build_target) {
					case AssemblyBuildTarget.StaticObject:
						LinkWithTaskOutput (link_dependencies); // Any .s or .ll files from the AOT compiler (compiled to object files)
						foreach (var info in infos) {
							LinkWithStaticLibrary (info.ObjectFiles);
							LinkWithStaticLibrary (info.BitcodeFiles);
						}
						continue; // no linking to do here.
					case AssemblyBuildTarget.DynamicLibrary:
						install_name = $"@executable_path/lib{name}.dylib";
						compiler_output = Path.Combine (Cache.Location, arch, $"lib{name}.dylib");
						break;
					case AssemblyBuildTarget.Framework:
						install_name = $"@rpath/{name}.framework/{name}";
						compiler_output = Path.Combine (Cache.Location, arch, $"lib{name}.dylib"); // frameworks are almost identical to dylibs, so this is expected.
						break;
					default:
						throw new Exception ();
					}

					CompileTask pinvoke_task;
					if (pinvoke_tasks.TryGetValue (abi, out pinvoke_task))
						link_dependencies.Add (pinvoke_task);

					foreach (var info in infos) {
						compiler_flags.AddLinkWith (info.ObjectFiles);
						compiler_flags.AddLinkWith (info.BitcodeFiles);
					}

					foreach (var task in link_dependencies)
						compiler_flags.AddLinkWith (task.OutputFile);

					foreach (var a in assemblies) {
						compiler_flags.AddFrameworks (a.Frameworks, a.WeakFrameworks);
						compiler_flags.AddLinkWith (a.LinkWith, a.ForceLoad);
						compiler_flags.AddOtherFlags (a.LinkerFlags);
					}
					compiler_flags.LinkWithMono ();
					compiler_flags.LinkWithXamarin ();
					if (GetEntryPoints ().ContainsKey ("UIApplicationMain"))
						compiler_flags.AddFramework ("UIKit");

					var link_task = new LinkTask ()
					{
						Target = this,
						AssemblyName = name,
						Abi = abi,
						OutputFile = compiler_output,
						InstallName = install_name,
						CompilerFlags = compiler_flags,
						Language = compiler_output.EndsWith (".s", StringComparison.Ordinal) ? "assembler" : null,
						SharedLibrary = build_target != AssemblyBuildTarget.StaticObject,
					};
					link_task.AddDependency (link_dependencies);
					link_task.AddDependency (aottasks);

					switch (build_target) {
					case AssemblyBuildTarget.StaticObject:
						LinkWithTaskOutput (link_task);
						break;
					case AssemblyBuildTarget.DynamicLibrary:
						AddToBundle (link_task.OutputFile);
						LinkWithTaskOutput (link_task);
						break;
					case AssemblyBuildTarget.Framework:
						AddToBundle (link_task.OutputFile, $"Frameworks/{name}.framework/{name}", dylib_to_framework_conversion: true);
						LinkWithTaskOutput (link_task);
						break;
					default:
						throw new Exception ();
					}

					foreach (var info in infos)
						info.LinkTask = link_task;
				}
			}

			if (Assemblies.All ((arg) => arg.HasDependencyMap)) {
				var dict = Assemblies.ToDictionary ((arg) => Path.GetFileNameWithoutExtension (arg.FileName));
				foreach (var asm in Assemblies) {
					if (!asm.HasDependencyMap)
						continue;

					if (asm.BuildTarget == AssemblyBuildTarget.StaticObject)
						continue;

					if (Profile.IsSdkAssembly (asm.AssemblyDefinition) || Profile.IsProductAssembly (asm.AssemblyDefinition)) {
						Console.WriteLine ("SDK assembly, so skipping assembly dependency checks: {0}", Path.GetFileNameWithoutExtension (asm.FileName));
						continue;
					}

					HashSet<Assembly> dependent_assemblies = new HashSet<Assembly> ();
					foreach (var dep in asm.DependencyMap) {
						Assembly dependentAssembly;
						if (!dict.TryGetValue (Path.GetFileNameWithoutExtension (dep), out dependentAssembly)) {
							Console.WriteLine ("Could not find dependency '{0}' of '{1}'", dep, asm.Identity);
							continue;
						}
						if (asm == dependentAssembly)
							continue; // huh?
						
						// Nothing can depend on anything in our SDK, nor does our SDK depend on anything else in our SDK
						// So we can remove any SDK dependency
						if (Profile.IsSdkAssembly (dependentAssembly.AssemblyDefinition) || Profile.IsProductAssembly (dependentAssembly.AssemblyDefinition)) {
							Console.WriteLine ("SDK assembly, so not a dependency of anything: {0}", Path.GetFileNameWithoutExtension (dependentAssembly.FileName));
							continue;
						}

						if (!dependentAssembly.HasLinkWithAttributes) {
							Console.WriteLine ("Assembly {0} does not have LinkWith attributes, so there's nothing we can depend on.", dependentAssembly.Identity);
							continue;
						}

						if (dependentAssembly.BuildTargetName == asm.BuildTargetName) {
							Console.WriteLine ("{0} is a dependency of {1}, but both are being built into the same target, so no dependency added.", Path.GetFileNameWithoutExtension (dep), Path.GetFileNameWithoutExtension (asm.FileName));
							continue;
						}

						Console.WriteLine ("Added {0} as a dependency of {1}", Path.GetFileNameWithoutExtension (dep), Path.GetFileNameWithoutExtension (asm.FileName));
						dependent_assemblies.Add (dependentAssembly);
					}
					foreach (var abi in Abis) {
						var target_task = asm.AotInfos [abi].LinkTask;
						var dependent_tasks = dependent_assemblies.Select ((v) => v.AotInfos [abi].LinkTask);

						var stack = new Stack<BuildTask> ();
						foreach (var dep in dependent_tasks) {
							stack.Clear ();
							stack.Push (target_task);
							if (target_task == dep || IsCircularTask (target_task, stack, dep)) {
								Console.WriteLine ("Found circular task.");
								Console.WriteLine ("Task {0} (with output {1}) depends on:", target_task.GetType ().Name, target_task.Outputs.First ());
								stack = new Stack<BuildTask> (stack.Reverse ());
								while (stack.Count > 0) {
									var node = stack.Pop ();
									Console.WriteLine ("   -> {0} (Output: {1})", node.GetType ().Name, node.Outputs.First ());
								}
							} else {
								target_task.AddDependency (dep);
								target_task.CompilerFlags.AddLinkWith (dep.OutputFile);
							}
						}
					}
				}
			}
		}

		bool IsCircularTask (BuildTask root, Stack<BuildTask> stack, BuildTask task)
		{
			stack.Push (task);

			foreach (var d in task?.Dependencies) {
				if (stack.Contains (d))
					return true;
				if (IsCircularTask (root, stack, d))
					return true;
			}
			stack.Pop ();

			return false;
		}

		public void Compile ()
		{
			// Compute the dependency map, and show warnings if there are any problems.
			List<Exception> exceptions = new List<Exception> ();
			foreach (var a in Assemblies)
				a.ComputeDependencyMap (exceptions);
			if (exceptions.Count > 0) {
				ErrorHelper.Show (exceptions);
				ErrorHelper.Warning (3006, "Could not compute a complete dependency map for the project. This will result in slower build times because Xamarin.iOS can't properly detect what needs to be rebuilt (and what does not need to be rebuilt). Please review previous warnings for more details.");
			}

			// Compile the managed assemblies into object files, frameworks or shared libraries
			AOTCompile ();

			List<string> registration_methods = new List<string> ();

			// The static registrar.
			if (App.Registrar == RegistrarMode.Static) {
				var registrar_m = Path.Combine (ArchDirectory, "registrar.m");
				var registrar_h = Path.Combine (ArchDirectory, "registrar.h");

				var run_registrar_task = new RunRegistrarTask
				{
					Target = this,
					RegistrarM = registrar_m,
					RegistrarH = registrar_h,
				};

				foreach (var abi in Abis) {
					var registrar_task = new CompileRegistrarTask
					{
						Target = this,
						Abi = abi,
						RegistrarM = registrar_m,
						RegistrarH = registrar_h,
						SharedLibrary = false,
						Language = "objective-c++",
						InputFile = registrar_m,
						OutputFile = Path.Combine (Cache.Location, abi.AsArchString (), Path.GetFileNameWithoutExtension (registrar_m) + ".o"),
						Dependency = run_registrar_task,
					};

					LinkWithTaskOutput (registrar_task);
				}

				registration_methods.Add ("xamarin_create_classes");
			}

			if (App.Registrar == RegistrarMode.Dynamic && App.IsSimulatorBuild && App.LinkMode == LinkMode.None) {
				string method;
				string library;
				switch (App.Platform) {
				case ApplePlatform.iOS:
					method = "xamarin_create_classes_Xamarin_iOS";
					library = "Xamarin.iOS.registrar.a";
					break;
				case ApplePlatform.WatchOS:
					method = "xamarin_create_classes_Xamarin_WatchOS";
					library = "Xamarin.WatchOS.registrar.a";
					break;					
				case ApplePlatform.TVOS:
					method = "xamarin_create_classes_Xamarin_TVOS";
					library = "Xamarin.TVOS.registrar.a";
					break;
				default:
					throw ErrorHelper.CreateError (71, "Unknown platform: {0}. This usually indicates a bug in Xamarin.iOS; please file a bug report at http://bugzilla.xamarin.com with a test case.", App.Platform);
				}

				registration_methods.Add (method);
				linker_flags.AddLinkWith (Path.Combine (Driver.MonoTouchLibDirectory, library));
			}

			// The main method.
			foreach (var abi in Abis) {
				var arch = abi.AsArchString ();
				var generate_main_task = new GenerateMainTask ()
				{
					Target = this,
					Abi = abi,
					MainM = Path.Combine (Cache.Location, arch, "main.m"),
					RegistrationMethods = registration_methods,
				};
				var main_task = new CompileMainTask
				{
					Target = this,
					Abi = abi,
					OutputFile = Path.Combine (Cache.Location, arch, "main.o"),
					InputFile = generate_main_task.MainM,
					Dependency = generate_main_task,
				};
				LinkWithTaskOutput (main_task);
			}

			Driver.Watch ("Compile", 1);
		}

		public void NativeLink (BuildTasks build_tasks)
		{
			if (!string.IsNullOrEmpty (App.UserGccFlags))
				App.DeadStrip = false;
			if (App.EnableLLVMOnlyBitCode)
				App.DeadStrip = false;

			// Get global frameworks
			linker_flags.AddFrameworks (App.Frameworks, App.WeakFrameworks);
			linker_flags.AddFrameworks (Frameworks, WeakFrameworks);

			// Collect all LinkWith flags and frameworks from all assemblies.
			foreach (var a in Assemblies) {
				linker_flags.AddFrameworks (a.Frameworks, a.WeakFrameworks);
				if (a.BuildTarget == AssemblyBuildTarget.StaticObject)
					linker_flags.AddLinkWith (a.LinkWith, a.ForceLoad);
				linker_flags.AddOtherFlags (a.LinkerFlags);

				if (a.BuildTarget == AssemblyBuildTarget.StaticObject) {
					foreach (var abi in Abis) {
						AotInfo info;
						if (!a.AotInfos.TryGetValue (abi, out info))
							continue;
						linker_flags.AddLinkWith (info.BitcodeFiles);
						linker_flags.AddLinkWith (info.ObjectFiles);
					}
				}
			}

			var bitcode = App.EnableBitCode;
			if (bitcode)
				linker_flags.AddOtherFlag (App.EnableMarkerOnlyBitCode ? "-fembed-bitcode-marker" : "-fembed-bitcode");
			
			if (App.EnablePie.HasValue && App.EnablePie.Value && (App.DeploymentTarget < new Version (4, 2)))
				ErrorHelper.Error (28, "Cannot enable PIE (-pie) when targeting iOS 4.1 or earlier. Please disable PIE (-pie:false) or set the deployment target to at least iOS 4.2");

			if (!App.EnablePie.HasValue)
				App.EnablePie = true;

			if (App.Platform == ApplePlatform.iOS) {
				if (App.EnablePie.Value && (App.DeploymentTarget >= new Version (4, 2))) {
					linker_flags.AddOtherFlag ("-Wl,-pie");
				} else {
					linker_flags.AddOtherFlag ("-Wl,-no_pie");
				}
			}

			CompileTask.GetArchFlags (linker_flags, Abis);
			if (App.IsDeviceBuild) {
				linker_flags.AddOtherFlag ($"-m{Driver.TargetMinSdkName}-version-min={App.DeploymentTarget}");
				linker_flags.AddOtherFlag ($"-isysroot {Driver.Quote (Driver.FrameworkDirectory)}");
			} else {
				CompileTask.GetSimulatorCompilerFlags (linker_flags, false, App);
			}
			linker_flags.LinkWithMono ();
			if (App.LibMonoLinkMode != AssemblyBuildTarget.StaticObject)
				AddToBundle (App.GetLibMono (App.LibMonoLinkMode));
			linker_flags.LinkWithXamarin ();
			if (App.LibXamarinLinkMode != AssemblyBuildTarget.StaticObject)
				AddToBundle (App.GetLibXamarin (App.LibXamarinLinkMode));

			linker_flags.AddOtherFlag ($"-o {Driver.Quote (Executable)}");

			linker_flags.AddOtherFlag ("-lz");
			linker_flags.AddOtherFlag ("-liconv");

			bool need_libcpp = false;
			if (App.EnableBitCode)
				need_libcpp = true;
#if ENABLE_BITCODE_ON_IOS
			need_libcpp = true;
#endif
			if (need_libcpp)
				linker_flags.AddOtherFlag ("-lc++");

			// allow the native linker to remove unused symbols (if the caller was removed by the managed linker)
			if (!bitcode) {
				foreach (var entry in GetRequiredSymbols ()) {
					// Note that we include *all* (__Internal) p/invoked symbols here
					// We also include any fields from [Field] attributes.
					linker_flags.ReferenceSymbol (entry);
				}
			}

			string mainlib;
			if (App.IsWatchExtension) {
				mainlib = "libwatchextension.a";
				linker_flags.AddOtherFlag (" -e _xamarin_watchextension_main");
			} else if (App.IsTVExtension) {
				mainlib = "libtvextension.a";
			} else if (App.IsExtension) {
				mainlib = "libextension.a";
			} else {
				mainlib = "libapp.a";
			}
			var libdir = Path.Combine (Driver.ProductSdkDirectory, "usr", "lib");
			var libmain = Path.Combine (libdir, mainlib);
			linker_flags.AddLinkWith (libmain, true);

			if (App.EnableProfiling) {
				string libprofiler;
				if (App.OnlyStaticLibraries) {
					libprofiler = Path.Combine (libdir, "libmono-profiler-log.a");
					linker_flags.AddLinkWith (libprofiler);
					if (!App.EnableBitCode)
						linker_flags.ReferenceSymbol ("mono_profiler_startup_log");
				} else {
					libprofiler = Path.Combine (libdir, "libmono-profiler-log.dylib");
					linker_flags.AddLinkWith (libprofiler);
					AddToBundle (libprofiler);
				}
			}

			if (!string.IsNullOrEmpty (App.UserGccFlags))
				linker_flags.AddOtherFlag (App.UserGccFlags);

			if (App.DeadStrip)
				linker_flags.AddOtherFlag ("-dead_strip");

			if (App.IsExtension) {
				if (App.Platform == ApplePlatform.iOS && Driver.XcodeVersion.Major < 7) {
					linker_flags.AddOtherFlag ("-lpkstart");
					linker_flags.AddOtherFlag ($"-F {Driver.Quote (Path.Combine (Driver.FrameworkDirectory, "System/Library/PrivateFrameworks"))} -framework PlugInKit");
				}
				linker_flags.AddOtherFlag ("-fapplication-extension");
			}

			var link_task = new NativeLinkTask ()
			{
				Target = this,
				OutputFile = Executable,
				CompilerFlags = linker_flags,
			};
			link_task.AddDependency (link_with_task_output);
			build_tasks.Add (link_task);
		}

		public void AdjustDylibs ()
		{
			var sb = new StringBuilder ();
			foreach (var dependency in Xamarin.MachO.GetNativeDependencies (Executable)) {
				if (!dependency.StartsWith ("/System/Library/PrivateFrameworks/", StringComparison.Ordinal))
					continue;
				var fixed_dep = dependency.Replace ("/PrivateFrameworks/", "/Frameworks/");
				sb.Append (" -change ").Append (dependency).Append (' ').Append (fixed_dep);
			}
			if (sb.Length > 0) {
				var quoted_name = Driver.Quote (Executable);
				sb.Append (' ').Append (quoted_name);
				Driver.XcodeRun ("install_name_tool", sb.ToString ());
				sb.Clear ();
			}
		}

		public bool CanWeSymlinkTheApplication ()
		{
			if (!Driver.CanWeSymlinkTheApplication ())
				return false;

			foreach (var a in Assemblies)
				if (!a.CanSymLinkForApplication ())
					return false;

			return true;
		}

		public void Symlink ()
		{
			foreach (var a in Assemblies)
				a.Symlink ();

			var targetExecutable = Executable;

			Application.TryDelete (targetExecutable);

			try {
				var launcher = new StringBuilder ();
				launcher.Append (Path.Combine (Driver.MonoTouchDirectory, "bin", "simlauncher"));
				if (Is32Build)
					launcher.Append ("32");
				else if (Is64Build)
					launcher.Append ("64");
				launcher.Append ("-sgen");
				File.Copy (launcher.ToString (), Executable);
				File.SetLastWriteTime (Executable, DateTime.Now);
			} catch (MonoTouchException) {
				throw;
			} catch (Exception ex) {
				throw new MonoTouchException (1015, true, ex, "Failed to create the executable '{0}': {1}", targetExecutable, ex.Message);
			}

			Symlinked = true;

			if (Driver.Verbosity > 0)
				Console.WriteLine ("Application ({0}) was built using fast-path for simulator.", string.Join (", ", Abis.ToArray ()));
		}

		public void StripManagedCode ()
		{
			var strip = false;

			strip = App.ManagedStrip && App.IsDeviceBuild && !App.EnableDebug && !App.PackageMdb;

			if (!Directory.Exists (AppTargetDirectory))
				Directory.CreateDirectory (AppTargetDirectory);

			if (strip) {
				// note: this is much slower when Parallel.ForEach is used
				Parallel.ForEach (Assemblies, new ParallelOptions () { MaxDegreeOfParallelism = Driver.Concurrency }, (assembly) => 
					{
						var file = assembly.FullPath;
						var output = Path.Combine (AppTargetDirectory, Path.GetFileName (assembly.FullPath));
						if (Application.IsUptodate (file, output)) {
							Driver.Log (3, "Target '{0}' is up-to-date", output);
						} else {
							Driver.FileDelete (output);
							Stripper.Process (file, output);
						}
						// The stripper will only copy the main assembly.
						// We need to copy .config files and satellite assemblies too
						if (App.PackageMdb)
							assembly.CopyMdbToDirectory (AppTargetDirectory);
						assembly.CopyConfigToDirectory (AppTargetDirectory);
						assembly.CopySatellitesToDirectory (AppTargetDirectory);
					});

				Driver.Watch ("Strip Assemblies", 1);
			} else if (!Symlinked) {
				foreach (var assembly in Assemblies)
					assembly.CopyToDirectory (AppTargetDirectory, reload: false, copy_mdb: App.PackageMdb);
			}
		}
	}
}
