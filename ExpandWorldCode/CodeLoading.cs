using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using ExpandWorld.Prefab;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Service;
namespace ExpandWorld.Code;

public class CodeLoading
{
  private static readonly string Pattern = "*.cs";

  public static void FromFile()
  {
    if (Helper.IsClient()) return;
    Load();
  }

  private static readonly Dictionary<string, MethodInfo> Functions = [];


  private static readonly List<MetadataReference> DefaultReferences = [];

  private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp13, DocumentationMode.Parse, SourceCodeKind.Regular, []);
  private static readonly CSharpCompilationOptions CompilationOptions = new(OutputKind.DynamicallyLinkedLibrary, false, null, null, null, [], OptimizationLevel.Release, false, true, null, null, default, null, Platform.AnyCpu, ReportDiagnostic.Default, 0, null, false, true, null, null, null, null, null, false, MetadataImportOptions.All);

  private static void Load()
  {
    if (Helper.IsClient()) return;
    if (DefaultReferences.Count == 0)
    {
      // Enables calling private methods.
      var topLevelBinderFlagsProperty = typeof(CSharpCompilationOptions).GetProperty("TopLevelBinderFlags", BindingFlags.Instance | BindingFlags.NonPublic);
      topLevelBinderFlagsProperty.SetValue(CompilationOptions, (uint)1 << 22);
      foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
      {
        if (assembly.Location == "") continue;
        DefaultReferences.Add(MetadataReference.CreateFromFile(assembly.Location));
      }
    }
    if (!Directory.Exists(Yaml.BaseDirectory))
      Directory.CreateDirectory(Yaml.BaseDirectory);
    var files = Directory.GetFiles(Yaml.BaseDirectory, Pattern, SearchOption.AllDirectories).ToArray();
    if (files.Length == 0)
    {
      if (Functions.Count > 0)
      {
        Log.Info($"Reloading code functions (0 entries).");
        Functions.Clear();
      }
      return;
    }

    Functions.Clear();

    foreach (var file in files)
    {
      var code = File.ReadAllText(file);
      var treee = CSharpSyntaxTree.ParseText(code, ParseOptions, file);
      var comp = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(file), [treee], DefaultReferences, CompilationOptions);
      using MemoryStream stream = new MemoryStream();
      var emitResult = comp.Emit(stream);
      if (!emitResult.Success)
      {
        foreach (var diagnostic in emitResult.Diagnostics)
          Log.Error($"Error compiling code from file {file}: {diagnostic.GetMessage()}");
        continue;
      }
      emitResult.Diagnostics.Clear();
      stream.Seek(0, SeekOrigin.Begin);
      var assembly = Assembly.Load(stream.ToArray());
      foreach (var type in assembly.GetTypes())
      {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
          Functions[method.Name] = method;
      }
    }
    if (Functions.Count == 0)
      return;
    Log.Info($"Reloading code functions ({Functions.Count} entries).");
  }

  public static string? Execute(string name) => Execute(name, []);
  public static string? Execute(string name, string arg) => Execute(name, arg.Split('_'));

  private static string? Execute(string name, string[] args)
  {
    if (!Functions.TryGetValue(name, out var method)) return null;
    var pars = method.GetParameters();
    var required = pars.Count(p => !p.IsOptional);
    if (required != args.Length)
    {
      Log.Error($"Method {name} expected requires {required} parameters, got {args.Length}.");
      return null;
    }
    var callArgs = args.Select((a, i) => ConvertType(a, pars[i].ParameterType)).ToArray();
    var result = method.Invoke(null, callArgs);
    return ConvertResult(result);
  }
  private static object ConvertType(string arg, Type type)
  {
    // TODO: Should check for unsuccessful conversions to return default value when inputs are not correct.
    if (type == typeof(string))
      return arg;
    if (type == typeof(int))
      return Parse.Int(arg);
    if (type == typeof(float))
      return Parse.Float(arg);
    if (type == typeof(bool))
      return bool.Parse(arg);
    return arg;
  }
  private static string ConvertResult(object result)
  {
    if (result is null)
      return "";
    if (result is string s)
      return s;
    if (result is int i)
      return i.ToString(CultureInfo.InvariantCulture);
    if (result is float f)
      return f.ToString(CultureInfo.InvariantCulture);
    if (result is bool b)
      return b.ToString();
    return result.ToString();
  }

  public static void SetupWatcher()
  {
    if (!Directory.Exists(Yaml.BaseDirectory))
      Directory.CreateDirectory(Yaml.BaseDirectory);
    Yaml.SetupWatcher(Pattern, FromFile, false);
  }


}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start)), HarmonyPriority(Priority.VeryLow)]
public class InitializeContent
{
  static void Postfix()
  {
    if (Helper.IsServer())
    {
      CodeLoading.FromFile();
    }

  }
}