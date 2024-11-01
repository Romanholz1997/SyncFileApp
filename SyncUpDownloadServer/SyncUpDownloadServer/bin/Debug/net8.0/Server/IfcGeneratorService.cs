using Xbim.Common;
using Xbim.Common.Step21;
using Xbim.Ifc;
using Xbim.IO;
using Xbim.Ifc4.Kernel;
using BGA.Core.Entities.PropertyEntity;
using BGA.Core.Services;
using System.Diagnostics;
using BGA.Core.Enums;
using BGA.Core.Entities.NearBuildingEntity;
using BGA.Core.Entities.TerrainEntity;
using BGA.Core.Helper.DAA.Core.Helpers;
using BGA.Core.Entities.NearStreetEntity;
using Newtonsoft.Json;
using BGA.Core.Entities.MainBuildingEntity;
using BGA.Core.Entities.FloorEntity;
using BGA.Core.Interfaces;
using BGA.Core.Exceptions;
using DocumentFormat.OpenXml.Wordprocessing;


namespace BGA.Core.Services
{
    public class IfcGeneratorService
    {
        private readonly IProgressManager _progressManager;
        public IfcGeneratorService(IProgressManager progressManager)
        {
            _progressManager = progressManager;
        }
        public List<string> GenerateBuilding(Property property, RoofType roofType, string projectId)
        {
            setCenterPoint originPoint = new setCenterPoint();
            List<Floor> floors = property.MainBuilding.Floors;
            originPoint.setCenter(Utils.GeometryToMercatorList(Utils.ParsePolygon(floors[0].Polygon)));
            List<string> models = new List<string>();
            //string ifcName = "MainBuilding.ifc";
            string ifcName = $"{Guid.NewGuid().ToString()}.ifc";
            string glb = "";
            using (var model = CreateandInitModel("VECTOPLAN"))
            {
                if (model != null)
                {
                    try
                    {
                        generatePogress generatePogress = new generatePogress();

                        BuildingService buildingService = new BuildingService(originPoint);

                        generatePogress.setPogress("BGA", 0, "Creating the Buildings", projectId);
                        _progressManager.SendProgressStatus(JsonConvert.SerializeObject(generatePogress));

                        buildingService.CreateMainBuilding(model, "MainBuilding", property.MainBuilding, roofType);
                        buildingService.CreateOutBuilding(model, "OutBuilding", property.OutBuilding, roofType);
                        buildingService.CreateNearStructure(model, "NearStructure", property.NearStructure);

                        generatePogress.setPogress("BGA", 0.2, "Successfully created Building", projectId);
                        _progressManager.SendProgressStatus(JsonConvert.SerializeObject(generatePogress));

                        buildingService.CreateNearBuilding(model, "NearBuilding", property.NearBuildings);
                        buildingService.CreateNearStreet(model, "NearStreet", property.NearStreets);
                        //buildingService.CreateTerrain(model, "Terrain", property);
                        model.SaveAs(ifcName, StorageType.Ifc);
                        generatePogress.setPogress("BGA", 0.5, "Successfully saved Ifc file", projectId);
                        _progressManager.SendProgressStatus(JsonConvert.SerializeObject(generatePogress));

                        HeaderEdit(ifcName);
                        glb = IfcConvert(ifcName, "ifc");
                        generatePogress.setPogress("BGA", 1, "Successfully converted Ifc file", projectId);
                        _progressManager.SendProgressStatus(JsonConvert.SerializeObject(generatePogress));

                    }
                    catch (ModelGenerateException ex)
                    {
                        throw new ModelGenerateException("GenerateBuilding()->" + ex.Message);
                    }
                }
            }
            using (var newModel = CreateandInitModel("VECTOPLAN"))
            {
                if (newModel != null)
                {
                    try
                    {
                        BuildingService building = new BuildingService(originPoint);
                        building.CreateMainBuilding(newModel, "MainBuilding", property.MainBuilding, roofType);
                        building.CreateOutBuilding(newModel, "OutBuilding", property.OutBuilding, roofType);
                        building.CreateNearStructure(newModel, "NearStructure", property.NearStructure);
                        building.CreateNearBuilding(newModel, "NearBuilding", property.NearBuildings);
                        building.CreateNearStreet(newModel, "NearStreet", property.NearStreets);
                        //building.CreateTerrain(newModel, "Terrain", property, 1);
                        newModel.SaveAs(ifcName, StorageType.Ifc);
                        HeaderEdit(ifcName);
                    }
                    catch (ModelGenerateException ex)
                    {
                        throw new ModelGenerateException("GenerateBuilding()->" + ex.Message);
                    }
                }
            }
            models.Add(ifcName);
            models.Add(glb);
            return models;
        }
        public void HeaderEdit(string filePath)
        {
            // Read the contents of the file
            string fileContents = File.ReadAllText(filePath);
            // Replace "text" with "word"
            string modifiedContents = fileContents.Replace(")),$);", ")));");
            modifiedContents = modifiedContents.Replace("FILE_DESCRIPTION ((''), '2;1');", "FILE_DESCRIPTION(('ViewDefinition [notYetAssigned]'),'2;1');");
            // Write the modified contents back to the file
            File.WriteAllText(filePath, modifiedContents);
            Console.WriteLine("File updated successfully!");
        }
        public string CreateTempFileName()
        {
            string tempDirectory = Path.GetTempPath();
            // Ensure the directory exists
            if (!Directory.Exists(tempDirectory))
            {
                Directory.CreateDirectory(tempDirectory);
            }
            //string file_name = Path.Combine(tempDirectory, Guid.NewGuid().ToString() + ".ifc");

            return tempDirectory;
        }
        public string IfcConvert(string ifcPath, string tempFolderPath)
        {
            try
            {
                //string ifcPath = Path.Combine(tempFolderPath, "MainBuilding.ifc");
                //string gltfPath = Path.Combine(tempFolderPath, $"{Guid.NewGuid().ToString()}.glb");
                string gltfPath = $"{Guid.NewGuid().ToString()}.glb";
                Process process = new Process();
                process.StartInfo.FileName = "IfcConvert";
                process.StartInfo.Arguments = $"{ifcPath} {gltfPath}";
                process.Start();
                //return gltfPath;
                process.WaitForExit();
                int exitCode = process.ExitCode;
                if (exitCode == 0)
                {
                    // The process completed successfully
                    Console.WriteLine("IFC to GLTF conversion completed successfully.");
                    return gltfPath;
                }
                else
                {
                    // The process encountered an error
                    Console.WriteLine($"IFC to GLTF conversion failed with exit code: {exitCode}");
                    return null;
                }
                //TempFolderDelete(tempFolderPath);
            }
            catch (Exception ex)
            {
                throw new ModelGenerateException("IfcConvert()->" + "there are errors while converting to glb ");
            }
        }
        public void TempFolderDelete(string tempFolderPath)
        {
            try
            {
                Directory.Delete(tempFolderPath, true);
            }
            catch (DirectoryNotFoundException)
            {
                Console.WriteLine($"The temporary folder '{tempFolderPath}' does not exist.");
            }
        }
        private static IfcStore CreateandInitModel(string projectName)
        {
            XbimEditorCredentials editorDetails = new XbimEditorCredentials
            {
                ApplicationDevelopersName = "Jany",
                ApplicationFullName = "IFC Generator",
                ApplicationIdentifier = "bga",
                ApplicationVersion = "1.0",
                EditorsFamilyName = "Team",
                EditorsGivenName = "hyatlas",
                EditorsOrganisationName = "realhyatlas"
            };
            //now we can create an IfcStore, it is in Ifc4 format and will be held in memory rather than in a database
            //database is normally better in performance terms if the model is large >50MB of Ifc or if robust transactions are required

            var model = IfcStore.Create(editorDetails, XbimSchemaVersion.Ifc4, XbimStoreType.InMemoryModel);

            //var fileDescription = model.Instances.New<IfcFileDescription>(fd =>
            //{
            //    fd.Description = new[] { "ViewDefinition [CoordinationView]" };
            //    fd.ImplementationLevel = "2;1";
            //});
            //Begin a transaction as all changes to a model are ACID
            using (var txn = model.BeginTransaction("Initialise Model"))
            {
                //create a project
                var project = model.Instances.New<IfcProject>();
                //set the units to SI (mm and metres)
                project.Initialize(ProjectUnits.SIUnitsUK);
                //project.GlobalId = Guid.NewGuid();
                project.Name = projectName;
                //now commit the changes, else they will be rolled back at the end of the scope of the using statement
                txn.Commit();
            }
            return model;
        }


    }
    public class generatePogress
    {
        public string Side { get; set; }
        public double Percent { get; set; }
        public string Description { get; set; }
        public string ProjectId { get; set; }
        public void setPogress(string side, double percent, string description, string project_id)
        {
            this.Side = side;
            this.Percent = percent;
            this.Description = description;
            this.ProjectId = project_id;
        }
        public void setJson(generatePogress generate)
        {
            string jsonString = JsonConvert.SerializeObject(generate);
            Console.WriteLine(jsonString);
        }
    }
    public class ExceptionToVectorAPI
    {
        //public string Side { get; set; }
        //public string Type { get; set; }
        //public string Description { get; set; }
        //public void setException(string type, string description, string side = "BGA")
        //{
        //    this.Side = side;
        //    this.Type = type;
        //    this.Description = description;


        //}
        public string errorCode { get; set; }
        public string errorMessage { get; set; }
        public string solution { get; set; }
        public string priority { get; set; }
        public string side { get; set; }
        public string ProjectId { get; set; }
        public void setException(string _errorCode, string _errorMessage, string _solution, string _priority, string _side = "BGA")
        {
            errorCode = _errorCode;
            errorMessage = _errorMessage;
            solution = _solution;
            priority = _priority;
            side = _side;
        }

    }



}
