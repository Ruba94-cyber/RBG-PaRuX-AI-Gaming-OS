using System.Security.AccessControl;

namespace PaRuX.Contracts;

/// <summary>
/// Lets PaRuX add an access rule to a folder or file whose DACL is not in Windows canonical order. Such lists are common on
/// machines shared with other agents, sandboxes or EDR products (an explicit entry stored after an inherited one), and .NET
/// refuses to modify them with "This access control list is not in canonical form and therefore cannot be modified".
/// </summary>
public static class AclCanonicalizer
{
    /// <summary>
    /// The same entries in canonical order (explicit denies, explicit allows, inherited denies, inherited allows). Nothing is added
    /// or removed and denies only ever move earlier, so the result is never more permissive than the original.
    /// </summary>
    public static T Canonicalized<T>(T security) where T : FileSystemSecurity, new()
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        RawAcl? dacl = raw.DiscretionaryAcl;
        if (dacl == null) return security;
        var explicitDeny = new List<GenericAce>(); var explicitAllow = new List<GenericAce>();
        var inheritedDeny = new List<GenericAce>(); var inheritedAllow = new List<GenericAce>();
        foreach (GenericAce ace in dacl)
        {
            bool inherited = (ace.AceFlags & AceFlags.Inherited) != 0;
            bool deny = ace.AceType is AceType.AccessDenied or AceType.AccessDeniedObject or AceType.AccessDeniedCallback or AceType.AccessDeniedCallbackObject;
            (inherited ? (deny ? inheritedDeny : inheritedAllow) : (deny ? explicitDeny : explicitAllow)).Add(ace);
        }
        var ordered = new RawAcl(dacl.Revision, dacl.Count);
        int index = 0;
        foreach (GenericAce ace in explicitDeny.Concat(explicitAllow).Concat(inheritedDeny).Concat(inheritedAllow)) ordered.InsertAce(index++, ace);
        raw.DiscretionaryAcl = ordered;
        byte[] binary = new byte[raw.BinaryLength];
        raw.GetBinaryForm(binary, 0);
        var fixedSecurity = new T();
        fixedSecurity.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access);
        return fixedSecurity;
    }

    /// <summary>
    /// Runs <paramref name="modify"/> on the security object; when .NET reports a non-canonical DACL it re-orders it first and retries
    /// once. Returns the object to write back. A DACL that is still unusable surfaces as the original <see cref="InvalidOperationException"/>.
    /// </summary>
    public static T Modify<T>(T security, Action<T> modify) where T : FileSystemSecurity, new()
    {
        try { modify(security); return security; }
        catch (InvalidOperationException)
        {
            T ordered = Canonicalized(security);
            modify(ordered);
            return ordered;
        }
    }

    public static T AddRule<T>(T security, FileSystemAccessRule rule) where T : FileSystemSecurity, new()
        => Modify(security, s => s.AddAccessRule(rule));
}
