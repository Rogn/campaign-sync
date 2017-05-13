﻿using System.Linq;
using System.Xml;
using Zone.Campaign.WebServices.Model.Abstract;

namespace Zone.Campaign.WebServices.Model
{
    /// <summary>
    /// Class representing a JavaScript file (xtk:javascript).
    /// </summary>
    [Schema(Schema)]
    public class Workflow : Persistable, IPersistable
    {
        #region Fields

        /// <summary>
        /// Schema represented by this class.
        /// </summary>
        public const string Schema = "xtk:workflow";

        #endregion

        #region Properties

        /// <summary>
        /// Internal name, combining namespace and name.
        /// </summary>
        public InternalName Name { get; set; }

        /// <summary>
        /// Label.
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// The raw xml data as a string.
        /// </summary>
        public string RawXml { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Formats the dataa into appropriate xml for sending in a persist request to Campaign.
        /// </summary>
        /// <param name="ownerDocument">Document to create the xml element from</param>
        /// <returns>Xml element containing all the properties to update</returns>
        public virtual XmlElement GetXmlForPersist(XmlDocument ownerDocument)
        {
            var element = GetBaseXmlForPersist(ownerDocument, "@internalName");
            element.AppendAttribute("internalName", Name.Name);

            if (Label != null)
            {
                element.AppendAttribute("label", Label);
            }

            var contentDoc = new XmlDocument();
            contentDoc.LoadXml(RawXml);
            foreach (var attribute in contentDoc.DocumentElement.Attributes.Cast<XmlAttribute>().ToArray())
            {
                element.AppendAttribute(attribute.Name, attribute.Value);
            }

            foreach (var child in contentDoc.DocumentElement.ChildNodes.Cast<XmlNode>().ToArray())
            {
                var importedNode = ownerDocument.ImportNode(child, true);
                element.AppendChild(importedNode);
            }

            return element;
        }

        #endregion
    }
}
