Code sharing with user frameworks
=================================

Xamarin.iOS can AOT-compile assemblies into:

* A static object (one static object per assembly).
* A dynamic library (one or more dynamic libraries per assembly).
  This is used for incremental builds (in which case it's one dynamic library per assembly).
* A user framework (one or more assemblies per framework).

The last case is interesting, because user frameworks can be used to share
code between extensions and the main app.

Xamarin.iOS leverages this to build one framework for all the assemblies in
the SDK, and this framework is shared between extensions and the main app.
This significantly reduces total code size of the app.

It's also possible to manually specify the assembly build target, by passing
--assembly-build-target to mtouch:

    --assembly-build-target=assembly=staticobject|dynamiclibrary|framework[=name]

The assembly name is the name of the assembly without the extension. The
assembly name can also be two special values:

* @all: all assemblies (that aren't already handled by other --assembly-build-target options).
* @sdk: all assemblies in the Xamarin.iOS SDK.

`@sdk` takes precedence over `all`, and any other named assembly takes
precedence over these special values.

If `name` is not specified, then it defaults to the assembly name. `name` can
also be duplicated, so compile multiple assemblies into a single framework or
dynamic library (but not static object).

Examples:

* --assembly-build-target=@all=dynamiclibrary

    This will compile every assembly into a separate dynamic library.

* --assembly-build-target=@all=framework=MyFramework

    This will compile all assemblies into one framework named `MyFramework`.

* --assembly-build-target=@sdk=framework=Xamarin.Sdk --assembly-build-target=@all=staticobject

    This will compile the Xamarin.iOS SDK into a single framework called
    `Xamarin.Sdk`, and all other assemblies into static objects.

* --assembly-build-target=MyAssembly1=framework=MyFramework --assembly-build-target=MyAssembly2=framework=MyFramework --assembly-build-target=@all=framework

    This will compile `MyAssembly1.dll` and `MyAssembly2.dll` into a single
    framework named `MyFramework`, and all other assemblies will be compiled
    into distinct frameworks.

* --assembly-build-target=@sdk=Framework=Xamarin.Sdk --assembly-build-target=@all=dynamiclibrary

   This will compile the Xamarin.iOS SDK into a single framework named
   `Xamarin.Sdk`, and every other assembly into dynamic libraries (one dynamic
   library per assembly).

Restrictions / limitations
--------------------------

* When compiling to static objects, every assembly must be compiled to a separate static object.

* Frameworks and dynamic libraries can't depend on static objects.

    An assembly compiled to a framework can't reference an assembly compiled to a static object.

    For example:

        * --assembly-build-target=mscorlib=staticobject --assembly-build-target=@all=framework

   This won't work, because every assembly depends on `mscorlib`, but
   `mscorlib` is compiled to a static object.

* Apple will not accept apps with dynamic libraries in the App Store. When building for release, build for either static object or framework.

* Apple recommends no more than 6 user frameworks in app, otherwise App Startup Time will suffer (link)[1].

    This means that compiling each assembly to a separate framework:

        --assembly-build-target=@all=framework

    is a bad idea, especially if your app has many assemblies.

[1]: https://developer.apple.com/videos/play/wwdc2016/406/


