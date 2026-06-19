using System;

namespace SprocketMultiplayer.Unused
{
    /// <summary>
    /// This attribute is used for files that are, obviously from the name, currently disabled and needless for the mod
    /// to function properly.
    /// Experimental code fragments and other stuff go here.
    /// Unused folder is ignored by git.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class Disabled : Attribute {
    }
}