using System.Runtime.InteropServices;

namespace Glance.App;

/// <summary>
/// Reads the current text selection via UI Automation COM.
/// Late-bound <c>dynamic</c> fails on CUIAutomation (no IDispatch for ElementFromPoint).
/// </summary>
internal static class UiaSelectionReader
{
    private const int UiaTextPatternId = 10014;
    private static readonly Guid ClsidCuiAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    public static string? TryRead(int screenX, int screenY)
    {
        object? uiaObj = null;
        try
        {
            var clsid = ClsidCuiAutomation;
            var iid = IidIUIAutomation;
            var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out uiaObj);
            if (hr != 0 || uiaObj is not IUIAutomation uia)
                return null;

            hr = uia.ElementFromPoint(new TagPoint { x = screenX, y = screenY }, out var elPtr);
            if (hr == 0 && elPtr != IntPtr.Zero)
            {
                var fromPoint = ReadSelection(elPtr);
                if (!string.IsNullOrWhiteSpace(fromPoint))
                    return fromPoint;
            }

            hr = uia.GetFocusedElement(out var focusedPtr);
            if (hr == 0 && focusedPtr != IntPtr.Zero)
                return ReadSelection(focusedPtr);

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (uiaObj is not null && Marshal.IsComObject(uiaObj))
                Marshal.ReleaseComObject(uiaObj);
        }
    }

    private static string? ReadSelection(IntPtr elementPtr)
    {
        IUIAutomationElement? element = null;
        try
        {
            element = (IUIAutomationElement)Marshal.GetTypedObjectForIUnknown(
                elementPtr, typeof(IUIAutomationElement));

            var hr = element.GetCurrentPattern(UiaTextPatternId, out var raw);
            if (hr != 0 || raw is null) return null;
            try
            {
                if (raw is not IUIAutomationTextPattern pattern)
                    return null;

                hr = pattern.GetSelection(out var rangesPtr);
                if (hr != 0 || rangesPtr == IntPtr.Zero) return null;
                IUIAutomationTextRangeArray? ranges = null;
                try
                {
                    ranges = (IUIAutomationTextRangeArray)Marshal.GetTypedObjectForIUnknown(
                        rangesPtr, typeof(IUIAutomationTextRangeArray));
                    hr = ranges.get_Length(out var length);
                    if (hr != 0 || length <= 0) return null;

                    hr = ranges.GetElement(0, out var rangePtr);
                    if (hr != 0 || rangePtr == IntPtr.Zero) return null;
                    IUIAutomationTextRange? range = null;
                    try
                    {
                        range = (IUIAutomationTextRange)Marshal.GetTypedObjectForIUnknown(
                            rangePtr, typeof(IUIAutomationTextRange));
                        hr = range.GetText(-1, out var text);
                        if (hr != 0 || string.IsNullOrWhiteSpace(text)) return null;
                        return text;
                    }
                    finally
                    {
                        if (range is not null && Marshal.IsComObject(range))
                            Marshal.ReleaseComObject(range);
                        else if (rangePtr != IntPtr.Zero)
                            Marshal.Release(rangePtr);
                    }
                }
                finally
                {
                    if (ranges is not null && Marshal.IsComObject(ranges))
                        Marshal.ReleaseComObject(ranges);
                    else if (rangesPtr != IntPtr.Zero)
                        Marshal.Release(rangesPtr);
                }
            }
            finally
            {
                if (Marshal.IsComObject(raw)) Marshal.ReleaseComObject(raw);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (element is not null && Marshal.IsComObject(element))
                Marshal.ReleaseComObject(element);
            else if (elementPtr != IntPtr.Zero)
                Marshal.Release(elementPtr);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct TagPoint
    {
        public int x;
        public int y;
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IntPtr el1, IntPtr el2, out int areSame);
        [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        [PreserveSig] int GetRootElement(out IntPtr root);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr element);
        [PreserveSig] int ElementFromPoint(TagPoint point, out IntPtr element);
        [PreserveSig] int GetFocusedElement(out IntPtr element);
    }

    [ComImport]
    [Guid("D22108BE-B695-4C3A-8767-1C1B4EC13B41")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IntPtr element);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IntPtr updated);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, out object value);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out object value);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, out object value);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out object value);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
    }

    [ComImport]
    [Guid("A543CC6B-B79C-4773-A7D8-0C8DCEE5D7CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        [PreserveSig] int RangeFromPoint(TagPoint point, out IntPtr range);
        [PreserveSig] int RangeFromChild(IntPtr child, out IntPtr range);
        [PreserveSig] int GetSelection(out IntPtr ranges);
    }

    [ComImport]
    [Guid("CE4A8ED4-C639-4636-B08B-A3E2B658AFE5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        [PreserveSig] int get_Length(out int pRetVal);
        [PreserveSig] int GetElement(int index, out IntPtr element);
    }

    [ComImport]
    [Guid("A543CC6A-B79C-4773-A7D8-0C8DCEE5D7CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        [PreserveSig] int Clone(out IntPtr clonedRange);
        [PreserveSig] int Compare(IntPtr range, out int areSame);
        [PreserveSig] int CompareEndpoints(int srcEndPoint, IntPtr range, int targetEndPoint, out int compValue);
        [PreserveSig] int ExpandToEnclosingUnit(int textUnit);
        [PreserveSig] int FindAttribute(int attr, object val, int backward, out IntPtr found);
        [PreserveSig] int FindText(
            [MarshalAs(UnmanagedType.BStr)] string text,
            int backward,
            int ignoreCase,
            out IntPtr found);
        [PreserveSig] int GetAttributeValue(int attr, out object value);
        [PreserveSig] int GetBoundingRectangles(out IntPtr boundingRects);
        [PreserveSig] int GetEnclosingElement(out IntPtr enclosingElement);
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
    }
}
