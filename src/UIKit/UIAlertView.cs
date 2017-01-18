//
// UIAlertView.cs: Eventsion to UIAlertView
//
// Authors:
//   Miguel de Icaza
//
// Copyright 2009, Novell, Inc.
// Copyright 2015 Xamarin Inc.
//

#if IOS

using System;
using XamCore.ObjCRuntime;
using XamCore.Foundation;

namespace XamCore.UIKit {
	public partial class UIAlertView {
		public UIAlertView (string title, string message, IUIAlertViewDelegate del, string cancelButtonTitle, params string [] otherButtons)
			: this (title, message, del, cancelButtonTitle, otherButtons == null || otherButtons.Length == 0 ? IntPtr.Zero : new NSString (otherButtons [0]).Handle, IntPtr.Zero, IntPtr.Zero)
		{
			if (otherButtons == null)
				return;

			// first button, if present, was already added
			for (int i = 1; i < otherButtons.Length; i++)
				AddButton (otherButtons [i]);
		}

		public UIAlertView (string title, string message, string cancelButtonTitle, params string [] otherButtons)
			: this (title, message, (IUIAlertViewDelegate) null, cancelButtonTitle, otherButtons)
		{
		}

#if !XAMCORE_4_0
		[Obsolete ("Use overload with a IUIAlertViewDelegate parameter")]
		public UIAlertView (string title, string message, UIAlertViewDelegate del, string cancelButtonTitle, params string [] otherButtons)
			: this (title, message, (IUIAlertViewDelegate) del, cancelButtonTitle, otherButtons)
		{
		}
#endif
	}
}

#endif // IOS
