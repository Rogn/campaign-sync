﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Zone.Campaign;
using Zone.Campaign.Templates.Model;
using Zone.Campaign.Templates.Services;
using Zone.Campaign.WebServices.Model;
using Zone.Campaign.WebServices.Security;
using Zone.Campaign.WebServices.Services;
using Zone.Campaign.WebServices.Services.Responses;
using CommandLine;
using log4net;
using log4net.Config;

namespace Zone.Campaign.Sync
{
    internal class Program
    {
        #region Fields

        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));

        private static readonly IDictionary<string, SchemaInfo> KnownSchemas = new Dictionary<string, SchemaInfo>
        {
            { "nms:includeView", new SchemaInfo(FileType.Html, "source/text") },
            { "xtk:form", new SchemaInfo(FileType.Xml, "@xtkschema") },
            { "xtk:javascript", new SchemaInfo(FileType.JavaScript, "data") },
            { "xtk:jst", new SchemaInfo(FileType.Html, "code") },
            { "xtk:srcSchema", new SchemaInfo(FileType.Xml, "@xtkschema") },
        };

        #endregion

        private static void Main(string[] args)
        {
            // Read options
            var options = new Options();
            if (!ValidateArguments(args, options))
            {
                // Parser will print out help about the failure
                return;
            }

            // Set up log4net
            XmlConfigurator.Configure();

            var rootUri = new Uri(options.Server);

            var sessionService = new Session(rootUri);
            var logonResponse = sessionService.Logon(options.Username, options.Password);

            // Logon failed - take no further action.
            if (logonResponse.Status != ResponseStatus.Success)
            {
                Log.WarnFormat("Logon failed: {0}, {1}", logonResponse.Status, logonResponse.Message);
                return;
            }

            var tokens = logonResponse.Data;

            // If download is specified, do download.
            if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                DoDownload(options, rootUri, tokens);
            }

            // If upload is specified, do upload.
            if (!string.IsNullOrEmpty(options.UploadListFilePath))
            {
                DoUpload(options, rootUri, tokens);
            }

            if (options.PromptToExit)
            {
                Console.WriteLine("Press any key to continue...");
                Console.ReadKey();
            }
        }

        #region Helpers

        private static bool ValidateArguments(string[] args, Options options)
        {
            if (!Parser.Default.ParseArguments(args, options))
            {
                // This will print out nicely formatted text for those errors picked up by the parser.
                return false;
            }

            var errors = new List<string>();

            // If downloading
            if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                // Check for unknown schema
                if (!KnownSchemas.ContainsKey(options.Schema))
                {
                    errors.Add(string.Format("Unrecognised schema: {0}. Known schemas are: {1}", options.Schema, string.Join(", ", KnownSchemas.Keys)));
                }
            }
            // If uploading
            else if (!string.IsNullOrEmpty(options.UploadListFilePath))
            {
                if (!File.Exists(options.UploadListFilePath))
                {
                    errors.Add(string.Format("File {0} not found.", options.UploadListFilePath));
                }
            }
            // If neither downloading nor uploading
            else
            {
                errors.Add("Either upload or download parameters must be specified.");
            }

            // Print out error messages.
            foreach (var error in errors)
            {
                Console.WriteLine(error);
            }

            return !errors.Any();
        }

        private static IMetadataInserter GetMetadataInserter(FileType fileType)
        {
            switch (fileType)
            {
                case FileType.Html:
                    return new HtmlMetadataProcessor();
                case FileType.JavaScript:
                    return new JavaScriptMetadataProcessor();
                case FileType.Xml:
                    return new XmlMetadataProcessor();
                default:
                    return null;
            }
        }

        private static void DoDownload(Options options, Uri rootUri, Tokens tokens)
        {
            var outDir = options.CreateSchemaSubdirectory
                             ? Path.Combine(options.OutputDirectory, options.Schema.Replace(":", "_"))
                             : options.OutputDirectory;
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            var schemaInfo = KnownSchemas[options.Schema];
            var queryDefService = new QueryDef(rootUri);
            var response = queryDefService.ExecuteQuery(tokens, options.Schema, new[]
            {
                "@name",
                "@label",
                schemaInfo.DataElement,
            }, options.Conditions);

            if (!response.Success)
            {
                Log.ErrorFormat("Query failed: {0}", response.Message);
                return;
            }

            Log.DebugFormat("Query succeeded:{0}{1}", Environment.NewLine, string.Join(",", response.Data));

            foreach (var item in response.Data)
            {
                var doc = new XmlDocument();
                doc.LoadXml(item);

                var namespaceElement = doc.DocumentElement.Attributes["namespace"];
                var metadata = new TemplateMetadata
                {
                    Schema = InternalName.Parse(options.Schema),
                    Name = new InternalName(namespaceElement == null ? null : namespaceElement.InnerText, doc.DocumentElement.Attributes["name"].InnerText),
                    Label = doc.DocumentElement.Attributes["label"].InnerText,
                };

                // TODO This selection of the code may need to change depending on specific format of returned xml.
                string rawCode;
                if (schemaInfo.FileType == FileType.Xml)
                {
                    // TODO: Remove the namespace by a better mechanism
                    rawCode = doc.DocumentElement.OuterXml.Replace(@" xmlns=""urn:xtk:queryDef""", string.Empty);
                }
                else
                {
                    XmlNode currentNode = doc.DocumentElement;
                    foreach (var name in schemaInfo.DataElement.Split(new[] { '/' }))
                    {
                        currentNode = currentNode.ChildNodes.Cast<XmlNode>().FirstOrDefault(i => i.LocalName == name);
                        if (currentNode == null)
                        {
                            break;
                        }
                    }

                    rawCode = currentNode == null
                                  ? string.Empty
                                  : currentNode.InnerText;
                }
                var template = new Template
                {
                    Metadata = metadata,
                    Code = rawCode,
                };

                var metadataInserter = GetMetadataInserter(schemaInfo.FileType);
                var code = metadataInserter.InsertMetadata(template);

                var filePath = metadata.Name.HasNamespace
                                   ? Path.Combine(outDir, metadata.Name.Namespace, metadata.Name.Name.Replace("_", @"\"))
                                   : Path.Combine(outDir, metadata.Name.Name.Replace("_", @"\"));
                if (!filePath.EndsWith(schemaInfo.FileType.GetFileExtension()))
                {
                    filePath += schemaInfo.FileType.GetFileExtension();
                }

                var dir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                //var filePath = Path.Combine(outDir, string.Format("{0}_{1}{2}", metadata.Name.Namespace, metadata.Name.Name, schemaInfo.FileType.GetFileExtension()));
                File.WriteAllText(filePath, code);
            }

            Log.InfoFormat("{0} files downloaded.", response.Data.Count());
        }

        private static void DoUpload(Options options, Uri rootUri, Tokens tokens)
        {
            IMetadataExtractorFactory metadataExtractorFactory = new MetadataProcessorFactory();
            ITemplateTransformerFactory templateTransformerFactory = new TemplateTransformerFactory();
            var persistService = new Persist(rootUri);

            var pathList = File.ReadAllLines(options.UploadListFilePath)
                               .Where(i => !string.IsNullOrWhiteSpace(i) && !i.StartsWith("#"))
                               .SelectMany(i =>
                               {
                                   if (File.Exists(i))
                                   {
                                       return new[] { i };
                                   }

                                   if (Directory.Exists(i))
                                   {
                                       return Directory.GetFiles(i);
                                   }

                                   var dir = Path.GetDirectoryName(i);
                                   if (Directory.Exists(dir))
                                   {
                                       return Directory.GetFiles(dir, Path.GetFileName(i));
                                   }

                                   Log.WarnFormat("{0} specified for upload but no matching files found.", i);
                                   return new string[0];
                               });
            var templateList = pathList.Select(i =>
            {
                var fileExtension = Path.GetExtension(i);
                var metadataExtractor = metadataExtractorFactory.GetExtractor(fileExtension);
                var templateTransformer = templateTransformerFactory.GetTransformer(fileExtension);

                var raw = File.ReadAllText(i);
                var template = metadataExtractor.ExtractMetadata(raw);
                template.Code = templateTransformer.Transform(template.Code, Path.GetDirectoryName(i));
                if (template.Metadata == null)
                {
                    Log.WarnFormat("No metadata found in {0}", i);
                }
                else if (template.Metadata.Schema == null)
                {
                    Log.WarnFormat("No schema found in {0}", i);
                }

                return template;
            }).Where(i => i.Metadata != null && i.Metadata.Schema != null);
            var groupedTemplates = templateList.GroupBy(i => i.Metadata.Schema.ToString());

            if (options.UploadTestMode)
            {
                foreach (var group in groupedTemplates)
                {
                    Log.InfoFormat("{1} {2} files found:{0}{3}", Environment.NewLine, group.Count(), group.Key, string.Join(Environment.NewLine, group.Select(i => i.Metadata.Name)));
                }
            }
            else
            {
                foreach (var group in groupedTemplates)
                {
                    switch (group.Key)
                    {
                        case "xtk:form":
                            {
                                var persistableItems = group.Select(i => new Form
                                {
                                    Name = i.Metadata.Name,
                                    Label = i.Metadata.Label,
                                    RawXml = i.Code,
                                });
                                //persistService.WriteCollection(tokens, persistableItems);
                                var count = 0;
                                foreach (var item in persistableItems)
                                {
                                    var response = persistService.Write(tokens, item);
                                    if (!response.Success)
                                    {
                                        Log.WarnFormat("Upload of {0} failed: {1}", item.Name, response.Message);
                                    }
                                    else
                                    {
                                        count++;
                                    }
                                }

                                Log.InfoFormat("{0} forms uploaded.", count);
                            }

                            break;
                        case "xtk:javascript":
                            {
                                var persistableItems = group.Select(i => new JavaScriptCode
                                {
                                    Name = i.Metadata.Name,
                                    Label = i.Metadata.Label,
                                    Data = i.Code,
                                });
                                //persistService.WriteCollection(tokens, persistableItems);
                                var count = 0;
                                foreach (var item in persistableItems)
                                {
                                    var response = persistService.Write(tokens, item);
                                    if (!response.Success)
                                    {
                                        Log.WarnFormat("Upload of {0} failed: {1}", item.Name, response.Message);
                                    }
                                    else
                                    {
                                        count++;
                                    }
                                }

                                Log.InfoFormat("{0} JavaScript codes uploaded.", count);
                            }

                            break;
                        case "xtk:jst":
                            {
                                var persistableItems = group.Select(i => new JavaScriptTemplate
                                {
                                    Name = i.Metadata.Name,
                                    Label = i.Metadata.Label,
                                    Code = i.Code,
                                });
                                //persistService.WriteCollection(tokens, persistableItems);
                                var count = 0;
                                foreach (var item in persistableItems)
                                {
                                    var response = persistService.Write(tokens, item);
                                    if (!response.Success)
                                    {
                                        Log.WarnFormat("Upload of {0} failed: {1}", item.Name, response.Message);
                                    }
                                    else
                                    {
                                        count++;
                                    }
                                }

                                Log.InfoFormat("{0} JavaScript templates uploaded.", count);
                            }

                            break;
                        case "xtk:srcSchema":
                            {
                                var persistableItems = group.Select(i => new SrcSchema
                                {
                                    Name = i.Metadata.Name,
                                    Label = i.Metadata.Label,
                                    RawXml = i.Code,
                                });
                                //persistService.WriteCollection(tokens, persistableItems);
                                var count = 0;
                                foreach (var item in persistableItems)
                                {
                                    var response = persistService.Write(tokens, item);
                                    if (!response.Success)
                                    {
                                        Log.WarnFormat("Upload of {0} failed: {1}", item.Name, response.Message);
                                    }
                                    else
                                    {
                                        count++;
                                    }
                                }

                                Log.InfoFormat("{0} schemas uploaded.", count);
                            }

                            break;
                    }
                }
            }
        }

        #endregion
    }
}