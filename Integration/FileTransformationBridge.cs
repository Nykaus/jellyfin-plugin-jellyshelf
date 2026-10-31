using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Jellyfin.Plugin.JellyShelf.Integration;

/// <summary>
/// Talks to the File Transformation plugin by reflection. Jellyfin plugins load
/// into separate assembly-load contexts, so that plugin cannot be referenced at
/// build time; when it is not installed, every call here is a harmless no-op and
/// the reader still works through its own URL.
/// </summary>
public class FileTransformationBridge
{
    private const string InterfaceTypeName = "Jellyfin.Plugin.FileTransformation.PluginInterface";

    private readonly Func<IEnumerable<Assembly>> _assemblies;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationBridge"/> class.
    /// </summary>
    public FileTransformationBridge()
    {
        _assemblies = () => AssemblyLoadContext.All.SelectMany(context => context.Assemblies);
    }

    /// <summary>
    /// Gets a value indicating whether the File Transformation plugin is loaded.
    /// </summary>
    public bool IsAvailable => FindInterface() is not null;

    /// <summary>
    /// Registers a <see cref="TransformCallbacks"/> method to transform a jellyfin-web
    /// file (matched by a regex on its path under <c>/web/</c>).
    /// </summary>
    /// <param name="id">A stable id for this registration.</param>
    /// <param name="fileNamePattern">A regular expression for the path under <c>/web/</c>.</param>
    /// <param name="callbackMethod">The name of a public static <see cref="TransformCallbacks"/> method.</param>
    /// <returns>Whether File Transformation accepted the registration.</returns>
    public bool Register(Guid id, string fileNamePattern, string callbackMethod)
    {
        var register = FindInterface()?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static);
        // Its single parameter is a Newtonsoft JObject; build it via the type's own Parse(string).
        var payloadType = register?.GetParameters() is [var parameter] ? parameter.ParameterType : null;
        var parse = payloadType?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (register is null || parse is null)
        {
            return false;
        }

        var json = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["id"] = id.ToString(),
            ["fileNamePattern"] = fileNamePattern,
            ["callbackAssembly"] = typeof(TransformCallbacks).Assembly.FullName,
            ["callbackClass"] = typeof(TransformCallbacks).FullName,
            ["callbackMethod"] = callbackMethod,
        });
        register.Invoke(null, [parse.Invoke(null, [json])]);
        return true;
    }

    /// <summary>
    /// Removes a registration made by <see cref="Register"/>.
    /// </summary>
    /// <param name="id">The registration id.</param>
    /// <returns>Whether File Transformation was present to remove it.</returns>
    public bool Remove(Guid id)
    {
        var remove = FindInterface()?.GetMethod("RemoveTransformation", BindingFlags.Public | BindingFlags.Static, [typeof(Guid)]);
        if (remove is null)
        {
            return false;
        }

        remove.Invoke(null, [id]);
        return true;
    }

    private Type? FindInterface() => _assemblies()
        .Select(assembly => assembly.GetType(InterfaceTypeName, throwOnError: false))
        .FirstOrDefault(type => type is not null);
}
