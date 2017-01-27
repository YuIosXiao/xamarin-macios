using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Diagnostics;
using System.Xml;

using NUnit.Framework;

namespace Xamarin.Tests
{
	class ToolMessage
	{
		public bool IsError;
		public bool IsWarning { get { return !IsError; } }
		public string Prefix;
		public int Number;
		public string PrefixedNumber { get { return Prefix + Number.ToString (); } }
		public string Message;
	//	public string Filename;
	//	public int LineNumber;
	}

	abstract class Tool
	{
		public bool Verbose;
		StringBuilder output = new StringBuilder ();

		List<string> output_lines;

		List<ToolMessage> messages = new List<ToolMessage> ();

		public Dictionary<string, string> EnvironmentVariables { get; set; }
		public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds (60);

		public IEnumerable<ToolMessage> Messages { get { return messages; } }
		List<string> OutputLines {
			get {
				if (output_lines == null) {
					output_lines = new List<string> ();
					output_lines.AddRange (output.ToString ().Split ('\n'));
				}
				return output_lines;
			}
		}

		protected abstract string ToolPath { get; }

		public int Execute (string arguments, params string [] args)
		{
			return Execute (ToolPath, arguments, args);
		}

		public int Execute (string toolPath, string arguments, params string [] args)
		{
			output.Clear ();
			output_lines = null;

			var rv = ExecutionHelper.Execute (toolPath, string.Format (arguments, args), EnvironmentVariables, output, output, Timeout);

			if (rv != 0 || Verbose) {
				if (output.Length > 0)
					Console.WriteLine ("\t" + output.ToString ().Replace ("\n", "\n\t"));
			}

			ParseMessages ();

			return rv;
		}

		void ParseMessages ()
		{
			messages.Clear ();

			foreach (var l in output.ToString ().Split ('\n')) {
				var line = l;
				var msg = new ToolMessage ();
				if (line.StartsWith ("error ", StringComparison.Ordinal)) {
					msg.IsError = true;
					line = line.Substring (6);
				} else if (line.StartsWith ("warning ", StringComparison.Ordinal)) {
					msg.IsError = false;
					line = line.Substring (8);
				} else {
					// something else
					continue;
				}
				if (line.Length < 7)
					continue; // something else
				msg.Prefix = line.Substring (0, 2);
				if (!int.TryParse (line.Substring (2, 4), out msg.Number))
					continue; // something else
				msg.Message = line.Substring (8);

				messages.Add (msg);
			}
		}

		public bool HasErrorPattern (string prefix, int number, string messagePattern)
		{
			foreach (var msg in messages) {
				if (msg.IsError && msg.Prefix == prefix && msg.Number == number && Regex.IsMatch (msg.Message, messagePattern))
					return true;
			}
			return false;
		}

		public bool HasError (string prefix, int number, string message)
		{
			foreach (var msg in messages) {
				if (msg.IsError && msg.Prefix == prefix && msg.Number == number && msg.Message == message)
					return true;
			}
			return false;
		}

		public void AssertErrorPattern (int number, string messagePattern)
		{
			AssertErrorPattern ("MT", number, messagePattern);
		}

		public void AssertErrorPattern (string prefix, int number, string messagePattern)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The error '{0}{1:0000}' was not found in the output.", prefix, number));

			if (messages.Any ((msg) => Regex.IsMatch (msg.Message, messagePattern)))
				return;

			var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && !Regex.IsMatch (msg.Message, messagePattern)).Select ((msg) => string.Format ("\tThe message '{0}' did not match the pattern '{1}'.", msg.Message, messagePattern));
			Assert.Fail (string.Format ("The error '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, messagePattern, string.Join ("\n", details.ToArray ())));
		}

		public void AssertError (int number, string message)
		{
			AssertError ("MT", number, message);
		}

		public void AssertError (string prefix, int number, string message)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The error '{0}{1:0000}' was not found in the output.", prefix, number));

			if (messages.Any ((msg) => msg.Message == message))
				return;

			var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && msg.Message != message).Select ((msg) => string.Format ("\tMessage #{2} did not match:\n\t\tactual:   '{0}'\n\t\texpected: '{1}'", msg.Message, message, messages.IndexOf (msg) + 1));
			Assert.Fail (string.Format ("The error '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, message, string.Join ("\n", details.ToArray ())));
		}

		public void AssertWarningPattern (int number, string messagePattern)
		{
			AssertWarningPattern ("MT", number, messagePattern);
		}

		public void AssertWarningPattern (string prefix, int number, string messagePattern)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The warning '{0}{1:0000}' was not found in the output.", prefix, number));

			if (messages.Any ((msg) => Regex.IsMatch (msg.Message, messagePattern)))
				return;

			var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && !Regex.IsMatch (msg.Message, messagePattern)).Select ((msg) => string.Format ("\tThe message '{0}' did not match the pattern '{1}'.", msg.Message, messagePattern));
			Assert.Fail (string.Format ("The warning '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, messagePattern, string.Join ("\n", details.ToArray ())));
		}

		public void AssertWarning (int number, string message)
		{
			AssertWarning ("MT", number, message);
		}

		public void AssertWarning (string prefix, int number, string message)
		{
			if (!messages.Any ((msg) => msg.Prefix == prefix && msg.Number == number))
				Assert.Fail (string.Format ("The warning '{0}{1:0000}' was not found in the output.", prefix, number));

			if (messages.Any ((msg) => msg.Message == message))
				return;

			var details = messages.Where ((msg) => msg.Prefix == prefix && msg.Number == number && msg.Message != message).Select ((msg) => string.Format ("\tMessage #{2} did not match:\n\t\tactual:   '{0}'\n\t\texpected: '{1}'", msg.Message, message, messages.IndexOf (msg) + 1));
			Assert.Fail (string.Format ("The warning '{0}{1:0000}: {2}' was not found in the output:\n{3}", prefix, number, message, string.Join ("\n", details.ToArray ())));
		}

		public void AssertNoWarnings ()
		{
			var warnings = messages.Where ((v) => v.IsWarning);
			if (!warnings.Any ())
				return;

			Assert.Fail ("No warnings expected, but got:\n{0}\t", string.Join ("\n\t", warnings.Select ((v) => v.Message).ToArray ()));
		}

		public bool HasOutput (string line)
		{
			return OutputLines.Contains (line);
		}

		public bool HasOutputPattern (string linePattern)
		{
			foreach (var line in OutputLines) {
				if (Regex.IsMatch (line, linePattern, RegexOptions.CultureInvariant))
					return true;
			}

			return false;
		}

		public void AssertOutputPattern (string linePattern)
		{
			if (!HasOutputPattern (linePattern))
				Assert.Fail (string.Format ("The output does not contain the line '{0}'", linePattern));
		}
	}

	class XBuild
	{
		public static void Build (string project, string configuration = "Debug", string platform = "iPhoneSimulator", string verbosity = null, TimeSpan? timeout = null)
		{
			var build = new BuildTool ()
			{
				ProjectPath = project,
				Config = configuration,
				Platform = platform,
				Verbosity = verbosity,
				Timeout = timeout,
			};
			build.Build ();
		}
	}

	class BuildTool
	{
		public bool UseMSBuild;
		public string ProjectPath;
		public string Config = "Debug";
		public string Platform = "iPhoneSimulator";
		public string Verbosity = "diagnostic";
		public string BaseIntermediateOutputPath;
		public Dictionary<string, string> Properties = new Dictionary<string, string> ();
		public TimeSpan? Timeout;

		string ToolPath {
			get {
				if (UseMSBuild) {
					return "/Library/Frameworks/Mono.framework/Commands/msbuild";
				} else {
					return "/Library/Frameworks/Mono.framework/Commands/xbuild";
				}
			}
		}

		public string Build ()
		{
			if (string.IsNullOrEmpty (BaseIntermediateOutputPath))
				BaseIntermediateOutputPath = Cache.CreateTemporaryDirectory ();
			if (BaseIntermediateOutputPath [BaseIntermediateOutputPath.Length - 1] != '/')
				BaseIntermediateOutputPath += "/";
			
			return RunTarget ("Build");
		}

		public string Clean (bool recursive = true)
		{
			var rv = RunTarget ("Clean");

			if (recursive) {
				// MSBuild doesn't do this automatically :(
				var paths = new HashSet<string> ();
				GetReferencedProjectPaths (ProjectPath, paths);
				foreach (var p in paths) {
					var path = p.Replace ('\\', '/');
					Console.WriteLine ("Cleaning referenced project: {0}", path);
					var tool = new BuildTool ();
					tool.ProjectPath = path;
					tool.Config = Config;
					tool.Platform = Platform;
					tool.Verbosity = Verbosity;
					tool.Timeout = Timeout;
					tool.Clean (false);
				}
			}

			return rv;
		}

		static void GetReferencedProjectPaths (string project_path, HashSet<string> paths)
		{
			var xml = new XmlDocument ();
			xml.Load (project_path);
			foreach (XmlNode node in xml.SelectNodes ("//*[local-name() = 'ProjectReference']/@Include")) {
				var full_path = Path.GetFullPath (Path.Combine (Path.GetDirectoryName (project_path), node.InnerText.Replace ('\\', '/')));
				if (paths.Add (full_path))
					GetReferencedProjectPaths (full_path, paths);
			}
		}

		string RunTarget (string target)
		{
			var env = new Dictionary<string, string> ();

			env ["MD_APPLE_SDK_ROOT"] = Path.GetDirectoryName (Path.GetDirectoryName (Configuration.xcode_root));
#if MONOTOUCH
			env ["MD_MTOUCH_SDK_ROOT"] = Configuration.SdkRootXI;
			env ["XBUILD_FRAMEWORK_FOLDERS_PATH"] = Configuration.XBuildFrameworkFoldersPathXI;
			env ["MSBuildExtensionsPath"] = Configuration.MSBuildExtensionsPathXI;
#else
			env ["XamarinMacFrameworkRoot"] = Configuration.SdkRootXM;
			env ["XAMMAC_FRAMEWORK_PATH"] = Configuration.SdkRootXM;
			env ["XBUILD_FRAMEWORK_FOLDERS_PATH"] = Configuration.XBuildFrameworkFoldersPathXM;
			env ["MSBuildExtensionsPath"] = Configuration.MSBuildExtensionsPathXM;
#endif

			var sb = new StringBuilder ();
			sb.Append ($"/t:{target} ");
			sb.Append ($"/p:Configuration={Config} ");
			sb.Append ($"/p:Platform={Platform} ");
			sb.Append ($"/verbosity:{Verbosity} ");
			if (!string.IsNullOrEmpty (BaseIntermediateOutputPath) && !Properties.ContainsKey ("BaseIntermediateOutputPath"))
				Properties ["BaseIntermediateOutputPath"] = BaseIntermediateOutputPath;
			foreach (var prop in Properties) {
				sb.Append ($"/p:'{prop.Key}={prop.Value}' ");
				if (prop.Value.IndexOfAny (new char [] { ':', ';', '=' }) >= 0)
					UseMSBuild = true; // xbuild can't parse /p=name=value where value contains equal signs.
			}

			if (UseMSBuild && !Properties.ContainsKey ("TargetFrameworkRootPath"))
				sb.Append (MTouch.Quote ($"/p:TargetFrameworkRootPath={env ["XBUILD_FRAMEWORK_FOLDERS_PATH"]}")).Append (' ');

			sb.Append (MTouch.Quote (ProjectPath));

			return ExecutionHelper.Execute (ToolPath, sb.ToString (), timeout: Timeout ?? TimeSpan.FromMinutes (10), environmentVariables: env);
		}
	}

	class XHarness
	{
		public static string ToolPath {
			get {
				return Path.Combine (Configuration.SourceRoot, "tests", "xharness", "xharness.exe");
			}
		}
	}

	static class ExecutionHelper {
		static int Execute (ProcessStartInfo psi, StringBuilder stdout, StringBuilder stderr, TimeSpan? timeout = null)
		{
			var watch = new Stopwatch ();
			watch.Start ();

			try {
				psi.UseShellExecute = false;
				psi.RedirectStandardError = true;
				psi.RedirectStandardOutput = true;
				foreach (System.Collections.DictionaryEntry envvar in psi.EnvironmentVariables) {
					var current = System.Environment.GetEnvironmentVariable ((string) envvar.Key);
					if (current == (string) envvar.Value)
						continue;
					Console.Write ($"{envvar.Key}={MTouch.Quote ((string) envvar.Value)} ");
				}
				Console.WriteLine ("{0} {1}", psi.FileName, psi.Arguments);
				using (var p = new Process ()) {
					p.StartInfo = psi;
					// mtouch/mmp writes UTF8 data outside of the ASCII range, so we need to make sure
					// we read it in the same format. This also means we can't use the events to get
					// stdout/stderr, because mono's Process class parses those using Encoding.Default.
					p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
					p.StartInfo.StandardErrorEncoding = Encoding.UTF8;
					p.Start ();

					var outReader = new Thread (() =>
						{
							string l;
							while ((l = p.StandardOutput.ReadLine ()) != null) {
								lock (stdout)
									stdout.AppendLine (l);
							}
						})
					{
						IsBackground = true,
					};
					outReader.Start ();

					var errReader = new Thread (() =>
						{
							string l;
							while ((l = p.StandardError.ReadLine ()) != null) {
								lock (stderr)
									stderr.AppendLine (l);
							}
						})
					{
						IsBackground = true,
					};
					errReader.Start ();

					if (timeout == null)
						timeout = TimeSpan.FromMinutes (5);
					if (!p.WaitForExit ((int) timeout.Value.TotalMilliseconds)) {
						Console.WriteLine ("Command didn't finish in {0} minutes:", timeout.Value.TotalMinutes);
						Console.WriteLine ("{0} {1}", p.StartInfo.FileName, p.StartInfo.Arguments);
						Console.WriteLine ("Will now kill the process");
						kill (p.Id, 9);
						if (!p.WaitForExit (1000 /* killing should be fairly quick */)) {
							Console.WriteLine ("Kill failed to kill in 1 second !?");
							return 1;
						}
					}

					outReader.Join (TimeSpan.FromSeconds (1));
					errReader.Join (TimeSpan.FromSeconds (1));

					return p.ExitCode;
				}
			} finally {
				Console.WriteLine ("{0} Executed in {1}: {2} {3}", DateTime.Now, watch.Elapsed.ToString (), psi.FileName, psi.Arguments);
			}
		}

		public static int Execute (string fileName, string arguments, out string output, TimeSpan? timeout = null)
		{
			var sb = new StringBuilder ();
			var psi = new ProcessStartInfo ();
			psi.FileName = fileName;
			psi.Arguments = arguments;
			var rv = Execute (psi, sb, sb, timeout);
			output = sb.ToString ();
			return rv;
		}

		public static int Execute (string fileName, string arguments, Dictionary<string, string> environmentVariables, StringBuilder stdout, StringBuilder stderr, TimeSpan? timeout = null)
		{
			if (stdout == null)
				stdout = new StringBuilder ();
			if (stderr == null)
				stderr = new StringBuilder ();

			var psi = new ProcessStartInfo ();
			psi.FileName = fileName;
			psi.Arguments = arguments;
			if (environmentVariables != null) {
				var envs = psi.EnvironmentVariables;
				foreach (var kvp in environmentVariables) {
					envs [kvp.Key] = kvp.Value;
				}
			}

			return Execute (psi, stdout, stderr, timeout);
		}

		[DllImport ("libc")]
		private static extern void kill (int pid, int sig);

		public static string Execute (string fileName, string arguments, bool throwOnError = true, Dictionary<string,string> environmentVariables = null,
			bool hide_output = false, TimeSpan? timeout = null
		)
		{
			StringBuilder output = new StringBuilder ();
			int exitCode = Execute (fileName, arguments, environmentVariables, output, output, timeout);
			if (!hide_output || (throwOnError && exitCode != 0)) {
				Console.WriteLine ("{0} {1}", fileName, arguments);
				Console.WriteLine (output);
				Console.WriteLine ("Exit code: {0}", exitCode);
			}
			if (throwOnError && exitCode != 0)
				throw new TestExecutionException (string.Format ("Execution failed for {0}: exit code = {1}", fileName, exitCode));
			return output.ToString ();
		}
	}

	class TestExecutionException : Exception {
		public TestExecutionException (string output)
			: base (output)
		{
		}
	}
}
