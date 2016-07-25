﻿using System;
using System.Collections;
using System.Linq;
using System.Threading;

using WatchKit;
using Foundation;

using NUnit.Framework.Internal.Filters;
using MonoTouch.NUnit.UI;

namespace monotouchtestWatchKitExtension
{
	[Register ("InterfaceController")]
	public partial class InterfaceController : WKInterfaceController
	{
		WatchOSRunner runner;
		bool running;

		[Action ("runTests:")]
		partial void RunTests (NSObject obj);

		[Outlet ("lblStatus")]
		WatchKit.WKInterfaceLabel lblStatus { get; set; }

		[Outlet ("lblSuccess")]
		WatchKit.WKInterfaceLabel lblSuccess { get; set; }

		[Outlet ("lblFailed")]
		WatchKit.WKInterfaceLabel lblFailed { get; set; }

		[Outlet ("lblIgnored")]
		WatchKit.WKInterfaceLabel lblIgnored { get; set; }

		[Outlet ("lblInconclusive")]
		WatchKit.WKInterfaceLabel lblInconclusive { get; set; }

		[Outlet ("cmdRun")]
		WatchKit.WKInterfaceButton cmdRun { get; set; }

		static InterfaceController ()
		{
			ObjCRuntime.Runtime.MarshalManagedException += (object sender, ObjCRuntime.MarshalManagedExceptionEventArgs args) =>
			{
				Console.WriteLine ("Managed exception: {0}", args.Exception);
			};
			ObjCRuntime.Runtime.MarshalObjectiveCException += (object sender, ObjCRuntime.MarshalObjectiveCExceptionEventArgs args) =>
			{
				Console.WriteLine ("Objective-C exception: {0}", args.Exception);
			};
		}

		public InterfaceController (IntPtr handle) : base (handle)
		{
		}

		public override void Awake (NSObject context)
		{
			base.Awake (context);

			BeginInvokeOnMainThread (LoadTests);
		}

		void LoadTests ()
		{
			runner = new WatchOSRunner ();
			var categoryFilter = new NotFilter (new CategoryExpression ("MobileNotWorking,NotOnMac,NotWorking,ValueAdd,CAS,InetAccess,NotWorkingInterpreter,WatchOSNotWorking").Filter);
			if (!string.IsNullOrEmpty (Environment.GetEnvironmentVariable ("NUNIT_FILTER_START"))) {
				var firstChar = Environment.GetEnvironmentVariable ("NUNIT_FILTER_START") [0];
				var lastChar = Environment.GetEnvironmentVariable ("NUNIT_FILTER_END") [0];
				var nameFilter = new NameStartsWithFilter () { FirstChar = firstChar, LastChar = lastChar };
				runner.Filter = new AndFilter (categoryFilter, nameFilter);
			} else {
				runner.Filter = categoryFilter;
			}
			runner.Add (GetType ().Assembly);
			BCL.Tests.TestLoader.AddTestAssemblies (runner);
			ThreadPool.QueueUserWorkItem ((v) =>
			{
				runner.LoadSync ();
				BeginInvokeOnMainThread (() =>
				{
					lblStatus.SetText (string.Format ("{0} tests", runner.TestCount));
					RenderResults ();
					cmdRun.SetEnabled (true);
					cmdRun.SetHidden (false);

					runner.AutoRun ();
				});
			});
		}

		void RunTests ()
		{
			if (running) {
				Console.WriteLine ("Already running");
				return;
			}
			running = true;
			cmdRun.SetEnabled (false);
			lblStatus.SetText ("Running");
			BeginInvokeOnMainThread (() => {
				runner.Run ();

				cmdRun.SetEnabled (true);
				lblStatus.SetText ("Done");
				BeginInvokeOnMainThread (RenderResults);
				running = false;
			});
		}

		void RenderResults ()
		{
			lblSuccess.SetText (string.Format ("Passed: {0}/{1} {2}%", runner.PassedCount, runner.TestCount, 100 * runner.PassedCount / runner.TestCount));
			lblFailed.SetText (string.Format ("Failed: {0}/{1} {2}%", runner.FailedCount, runner.TestCount, 100 * runner.FailedCount / runner.TestCount));
			lblIgnored.SetText (string.Format ("Ignored: {0}/{1} {2}%", runner.IgnoredCount, runner.TestCount, 100 * runner.IgnoredCount / runner.TestCount));
			lblInconclusive.SetText (string.Format ("Inconclusive: {0}/{1} {2}%", runner.InconclusiveCount, runner.TestCount, 100 * runner.InconclusiveCount / runner.TestCount));
		}

		partial void RunTests (NSObject obj)
		{
			RunTests ();
		}
	}
}

class NameStartsWithFilter : NUnit.Framework.Internal.TestFilter
{
	public char FirstChar;
	public char LastChar;

	public override bool Match (NUnit.Framework.Api.ITest test)
	{
		if (test is NUnit.Framework.Internal.TestAssembly)
			return true;

		var method = test as NUnit.Framework.Internal.TestMethod;
		if (method != null)
			return Match (method.Parent);
		
		var name = !string.IsNullOrEmpty (test.Name) ? test.Name : test.FullName;
		bool rv;
		if (string.IsNullOrEmpty (name)) {
			rv = true;
		} else {
			var z = Char.ToUpperInvariant (name [0]);
			rv = z >= Char.ToUpperInvariant (FirstChar) && z <= Char.ToUpperInvariant (LastChar);
		}

		return rv;
	}

	public override bool Pass (NUnit.Framework.Api.ITest test)
	{
		return Match (test);
	}
}
