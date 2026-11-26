using System.Linq;
using Avalonia.Ide.CompletionEngine;
using Avalonia.Ide.CompletionEngine.Navigation;

namespace AvaloniaVS.Navigation
{
    /// <summary>
    /// The event an attribute subscribes to: <c>Click</c> on a <c>Button</c>, or an attached event
    /// such as <c>Gestures.Tapped</c>, plus the <c>x:Class</c> that will hold the handler.
    /// </summary>
    internal sealed class EventAttributeContext
    {
        public XamlElement Element { get; private set; }

        public XamlAttribute Attribute { get; private set; }

        /// <summary>Full name of the type that declares the event.</summary>
        public string EventOwnerType { get; private set; }

        public string EventName { get; private set; }

        public bool IsAttached { get; private set; }

        /// <summary>Full name of the root element's <c>x:Class</c>, or null.</summary>
        public string ClassName { get; private set; }

        /// <summary>
        /// Gets the event attribute at <paramref name="position"/>, or null if the position isn't
        /// in an event attribute's value. Uses <paramref name="helper"/> (which must have metadata
        /// loaded for this document) to tell events from properties.
        /// </summary>
        public static EventAttributeContext TryCreate(XamlDocument document, int position, CompletionEngine.MetadataHelper helper)
        {
            var element = XamlScanner.FindStartTagAt(document.Elements, position);
            var attribute = element?.Attributes.LastOrDefault(a => a.HasValue && a.ValueStart <= position && position <= a.ValueEnd + 1);
            return element is null || attribute is null ? null : TryCreate(document, element, attribute, helper);
        }

        public static EventAttributeContext TryCreate(XamlDocument document, XamlElement element, XamlAttribute attribute, CompletionEngine.MetadataHelper helper)
        {
            if (helper?.Metadata is null)
            {
                return null;
            }

            var name = attribute.Name;
            var dot = name.IndexOf('.', name.IndexOf(':') + 1);
            string ownerType;
            string eventName;

            if (dot > 0)
            {
                var owner = helper.LookupType(name.Substring(0, dot));
                eventName = name.Substring(dot + 1);
                var attachedName = eventName;
                if (owner?.Events.Any(e => e.IsAttached && e.Name == attachedName) != true)
                {
                    return null;
                }

                ownerType = owner.FullName;
            }
            else
            {
                eventName = name;
                var type = helper.LookupType(element.Name);
                var metadataEvent = type?.Events.FirstOrDefault(e => !e.IsAttached && e.Name == name);
                if (metadataEvent is null)
                {
                    return null;
                }

                ownerType = metadataEvent.DeclaringType?.FullName is { Length: > 0 } declaring ? declaring : type.FullName;
            }

            return new EventAttributeContext
            {
                Element = element,
                Attribute = attribute,
                EventOwnerType = ownerType,
                EventName = eventName,
                IsAttached = dot > 0,
                ClassName = document.Root?.GetXamlDirective("Class")?.Value?.Trim(),
            };
        }
    }
}
