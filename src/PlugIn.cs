//  Authors:    Arjan de Bruijn
//              Brian R. Miranda

// John McNabb: (02.04.2019)
//
//  Summary of changes to allow the climate library to be used with PnET-Succession:
//   (1) Added ClimateRegionData class based on that of NECN to hold the climate library data. This is Initialized by a call
//       to InitialClimateLibrary() in Plugin.Initialize().
//   (2) Modified EcoregionPnET to add GetClimateRegionData() which grabs climate data from ClimateRegionData.  This uses an intermediate
//       MonthlyClimateRecord instance which is similar to ObservedClimate.
//       MonthlyClimateRecord instance which is similar to ObservedClimate.
//   (3) Added ClimateRegionPnETVariables class which is a copy of the EcoregionPnETVariables class which uses MonthlyClimateRecord rather than
//       ObserverdClimate. I had hoped to use the same class, but the definition of IObservedClimate prevents MonthlyClimateRecord from implementing it.
//       IMPORTANT NOTE: The climate library precipation is in cm/month, so that it is converted to mm/month in MonthlyClimateRecord.
//   (4) Modified Plugin.AgeCohorts() and SiteCohorts.SiteCohorts() to call either EcoregionPnET.GetClimateRegoinData() or EcoregionPnET.GetData()
//       depending on whether the climate library is enabled.

//   Enabling the climate library with PnET:
//   (1) Indicate the climate library configuration file in the 'PnET-succession' configuration file using the 'ClimateConfigFile' parameter, e.g.
//        ClimateConfigFile	"./climate-generator-baseline.txt"
//
//   NOTE: Use of the climate library is OPTIONAL.  If the 'ClimateConfigFile' parameter is missing (or commented-out) of the 'PnET-succession'
//   configuration file, then PnET reverts to using climate data as specified by the 'ClimateFileName' column in the 'EcoregionParameters' file
//   given in the 'PnET-succession' configuration file.
//
//   NOTE: This uses a version (v4?) of the climate library that exposes AnnualClimate_Monthly.MonthlyOzone[] and .MonthlyCO2[].

using Landis.Core;
using Landis.Library.InitialCommunities.Universal;
using Landis.Library.PnETCohorts;
using Landis.Library.Succession;
using Landis.SpatialModeling;
using Landis.Library.Climate;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Landis.Library.Succession.DensitySeeding;


namespace Landis.Extension.Succession.BiomassPnET
{
    public class PlugIn  : Landis.Library.Succession.ExtensionBase 
    {
        public static SpeciesPnET SpeciesPnET;
        //public static ISiteVar<float[]> MonthlyPressureHead;
        //public static ISiteVar<SortedList<float, float>[]> MonthlySoilTemp;
        //public static ISiteVar<float> FieldCapacity;
        public static DateTime Date;
        public static ICore ModelCore;
        private static DateTime StartDate;
        private static Dictionary<ActiveSite, string> SiteOutputNames;
        public static float FTimeStep;
        public static bool UsingClimateLibrary;
        private Dictionary<ActiveSite, ICommunity> sitesAndCommunities;
        public static string InitialCommunitiesSpinup;
        public static int CohortBinSize;
        public static int ParallelThreads;
        private static readonly object threadLock = new object();
        private Dictionary<ActiveSite, uint> allKeys;
        public static float MinFolRatioFactor;

        MyClock m = null;
        //---------------------------------------------------------------------
        public void DeathEvent(object sender, Landis.Library.UniversalCohorts.DeathEventArgs eventArgs)
        {
            ExtensionType disturbanceType = eventArgs.DisturbanceType;
            if (disturbanceType != null)
            {
                ActiveSite site = eventArgs.Site;
                if (disturbanceType.IsMemberOf("disturbance:fire"))
                    Reproduction.CheckForPostFireRegen(eventArgs.Cohort, site);
                else
                    Reproduction.CheckForResprouting(eventArgs.Cohort, site);
            }
        }
        //---------------------------------------------------------------------
        string PnETDefaultsFolder
        {
            get
            {
                string defaultPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "Defaults");
                // If Linux, correct the path string
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                {
                    defaultPath = defaultPath.Replace('\\', '/');
                }
                return defaultPath;
            }
        }
        //---------------------------------------------------------------------
        public PlugIn()
            : base(Names.ExtensionName)
        {
            LocalOutput.PNEToutputsites = Names.PNEToutputsites;

            // The number of thread workers to use in succession routines that have been optimized. Should
            // more or less match the number of cores in the computer thats running LANDIS-II's processor
            //this.ThreadCount = 3;
            //this.ThreadCount = 1;

            allKeys = new Dictionary<ActiveSite, uint>();
            sitesAndCommunities = new Dictionary<ActiveSite, ICommunity>();
        }
        //---------------------------------------------------------------------
        public override void LoadParameters(string InputParameterFile, ICore mCore)
        {
            ModelCore = mCore;

            Names.parameters.Add(Names.ExtensionName, new Parameter<string>(Names.ExtensionName, InputParameterFile));

            //-------------PnET-Succession input files
            Dictionary<string, Parameter<string>> InputParameters = Names.LoadTable(Names.ExtensionName, Names.AllNames, null, true);
            InputParameters.ToList().ForEach(x => Names.parameters.Add(x.Key, x.Value));

            //-------------Read Species parameters input file
            List<string> SpeciesNames = PlugIn.ModelCore.Species.ToList().Select(x => x.Name).ToList();
            List<string> SpeciesPars = SpeciesPnET.ParameterNames;
            SpeciesPars.Add(Names.PnETSpeciesParameters);
            Dictionary<string, Parameter<string>> speciesparameters = Names.LoadTable(Names.PnETSpeciesParameters, SpeciesNames, SpeciesPars);
            foreach (string key in speciesparameters.Keys)
            {
                if (Names.parameters.ContainsKey(key)) throw new System.Exception("Parameter " + key + " was provided twice");
            }
            speciesparameters.ToList().ForEach(x => Names.parameters.Add(x.Key, x.Value));

            //-------------Ecoregion parameters
            List<string> EcoregionNames = PlugIn.ModelCore.Ecoregions.ToList().Select(x => x.Name).ToList();
            List<string> EcoregionParameters = EcoregionData.ParameterNames;
            Dictionary<string, Parameter<string>> ecoregionparameters = Names.LoadTable(Names.EcoregionParameters, EcoregionNames, EcoregionParameters);
            foreach (string key in ecoregionparameters.Keys)
            {
                if (Names.parameters.ContainsKey(key)) throw new System.Exception("Parameter "+ key +" was provided twice");
            }
            ecoregionparameters.ToList().ForEach(x => Names.parameters.Add(x.Key, x.Value));

            //-------------DisturbanceReductionsParameterFile
            Parameter<string> DisturbanceReductionsParameterFile;
            if (Names.TryGetParameter(Names.DisturbanceReductions, out DisturbanceReductionsParameterFile))
            {
                Allocation.Initialize(DisturbanceReductionsParameterFile.Value, Names.parameters);
                Cohort.AgeOnlyDeathEvent += DisturbanceReductions.Events.CohortDied;
            }
             
            //---------------SaxtonAndRawlsParameterFile
            if (Names.parameters.ContainsKey(PressureHeadSaxton_Rawls.SaxtonAndRawlsParameters) == false)
            {
                Parameter<string> SaxtonAndRawlsParameterFile = new Parameter<string>(PressureHeadSaxton_Rawls.SaxtonAndRawlsParameters, (string)PnETDefaultsFolder + System.IO.Path.DirectorySeparatorChar + "SaxtonAndRawlsParameters.txt");
                Names.parameters.Add(PressureHeadSaxton_Rawls.SaxtonAndRawlsParameters, SaxtonAndRawlsParameterFile);
            }
            Dictionary<string, Parameter<string>> SaxtonAndRawlsParameters = Names.LoadTable(PressureHeadSaxton_Rawls.SaxtonAndRawlsParameters, null, PressureHeadSaxton_Rawls.ParameterNames);
            foreach (string key in SaxtonAndRawlsParameters.Keys)
            {
                if (Names.parameters.ContainsKey(key)) throw new System.Exception("Parameter " + key + " was provided twice");
            }
            SaxtonAndRawlsParameters.ToList().ForEach(x => Names.parameters.Add(x.Key, x.Value));

            //--------------PnETGenericParameterFile
            //----------See if user supplied overwriting default parameters
            List<string> RowLabels = new List<string>(Names.AllNames);
            RowLabels.AddRange(SpeciesPnET.ParameterNames); 

            if (Names.parameters.ContainsKey(Names.PnETGenericParameters))
            {
                Dictionary<string, Parameter<string>> genericparameters = Names.LoadTable(Names.PnETGenericParameters,  RowLabels, null, true);
                foreach (KeyValuePair<string, Parameter<string>> par in genericparameters)
                {
                    if (Names.parameters.ContainsKey(par.Key)) throw new System.Exception("Parameter " + par.Key + " was provided twice");
                    Names.parameters.Add(par.Key, par.Value);
                }
            }

            //----------Load in default parameters to fill the gaps
            Parameter<string> PnETGenericDefaultParameterFile = new Parameter<string>(Names.PnETGenericDefaultParameters, (string)PnETDefaultsFolder + System.IO.Path.DirectorySeparatorChar + "PnETGenericDefaultParameters.txt");
            Names.parameters.Add(Names.PnETGenericDefaultParameters, PnETGenericDefaultParameterFile);
            Dictionary<string, Parameter<string>> genericdefaultparameters = Names.LoadTable(Names.PnETGenericDefaultParameters, RowLabels, null, true);

            foreach (KeyValuePair<string, Parameter<string>> par in genericdefaultparameters)
            {
                if (Names.parameters.ContainsKey(par.Key) == false)
                {
                    Names.parameters.Add(par.Key, par.Value);
                }
            }

            SiteOutputNames = new Dictionary<ActiveSite, string>();
            Parameter<string> OutputSitesFile;
            if (Names.TryGetParameter(LocalOutput.PNEToutputsites, out OutputSitesFile))
            {
                Dictionary<string, Parameter<string>> outputfiles = Names.LoadTable(LocalOutput.PNEToutputsites, null, AssignOutputFiles.ParameterNames.AllNames, true);
                AssignOutputFiles.MapCells(outputfiles, ref SiteOutputNames);
            }
        }
        //---------------------------------------------------------------------
        public override void Initialize()
        {
            PlugIn.ModelCore.UI.WriteLine("Initializing " + Names.ExtensionName + " version " + typeof(PlugIn).Assembly.GetName().Version);
            Cohort.DeathEvent += DeathEvent;
            StartDate = new DateTime(((Parameter<int>)Names.GetParameter(Names.StartYear)).Value, 1, 15);
            Globals.InitializeCore(ModelCore, ((Parameter<ushort>)Names.GetParameter(Names.IMAX)).Value, StartDate);
            EcoregionData.Initialize();
            SiteVars.Initialize();

            //MonthlyPressureHead = ModelCore.Landscape.NewSiteVar<float[]>();
            //MonthlySoilTemp = ModelCore.Landscape.NewSiteVar<SortedList<float, float>[]>();
            //FieldCapacity = ModelCore.Landscape.NewSiteVar<float>();
            Landis.Utilities.Directory.EnsureExists("output");

            Timestep = ((Parameter<int>)Names.GetParameter(Names.Timestep)).Value;
            Parameter<string> CohortBinSizeParm = null;
            if (Names.TryGetParameter(Names.CohortBinSize, out CohortBinSizeParm))
            {
                if (Int32.TryParse(CohortBinSizeParm.Value, out CohortBinSize))
                {
                    if(CohortBinSize < Timestep)
                    {
                        throw new System.Exception("CohortBinSize cannot be smaller than Timestep.");
                    }
                    else
                        PlugIn.ModelCore.UI.WriteLine("  Succession timestep = " + Timestep + "; CohortBinSize = " + CohortBinSize + ".");
                }
                else
                {
                    throw new System.Exception("CohortBinSize is not an integer value.");
                }
            }
            else
                CohortBinSize = Timestep;

            string Parallel = ((Parameter<string>)Names.GetParameter(Names.Parallel)).Value;
            if (Parallel == "false")
            {
                ParallelThreads = 1;
                PlugIn.ModelCore.UI.WriteLine("  MaxParallelThreads = " + ParallelThreads.ToString() + ".");
            }
            else if (Parallel == "true")
            {
                ParallelThreads = -1;
                PlugIn.ModelCore.UI.WriteLine("  MaxParallelThreads determined by system.");
            }
            else
            {
                if (Int32.TryParse(Parallel, out ParallelThreads))
                {
                    if (ParallelThreads < 1)
                    {
                        throw new System.Exception("Parallel cannot be < 1.");
                    }
                    else
                    {
                        PlugIn.ModelCore.UI.WriteLine("  MaxParallelThreads = " + ParallelThreads.ToString() + ".");
                    }
                }else
                {
                    throw new System.Exception("Parallel must be 'true', 'false' or an integer >= 1.");
                }
            }
            this.ThreadCount = ParallelThreads;

            FTimeStep = 1.0F / Timestep;
            if(!Names.TryGetParameter(Names.ClimateConfigFile, out var climateLibraryFileName))
            {
                PlugIn.ModelCore.UI.WriteLine($"  No ClimateConfigFile provided. Using climate files in ecoregion parameters: {Names.parameters["EcoregionParameters"].Value}.");
                ObservedClimate.Initialize();
            }
            SpeciesPnET = new SpeciesPnET();
            Landis.Library.PnETCohorts.SpeciesParameters.LoadParameters(SpeciesPnET);

            Hydrology.Initialize();
            SiteCohorts.Initialize();
            string PARunits = ((Parameter<string>)Names.GetParameter(Names.PARunits)).Value;
            if (PARunits != "umol" && PARunits != "W/m2")
            {
                throw new System.Exception("PARunits are not 'umol' or 'W/m2'.");
            }
            //string ETMethod = ((Parameter<string>)Names.GetParameter(Names.ETMethod)).Value;
            /*if (ETMethod != "Original" && ETMethod != "Radiation" && ETMethod != "WATER" && ETMethod != "WEPP")
            {
                throw new System.Exception("ETMethod is not 'Original' or 'Radiation' or 'WATER' or 'WEPP'.");
            }*/
            InitializeClimateLibrary(StartDate.Year); // John McNabb: initialize climate library after EcoregionPnET has been initialized
            //EstablishmentProbability.Initialize(Timestep);  // Not used

            // Initialize Reproduction routines:
            Reproduction.SufficientResources = SufficientResources;
            Reproduction.Establish = Establish;
            Reproduction.AddNewCohort = AddNewCohort;
            Reproduction.MaturePresent = MaturePresent;
            Reproduction.PlantingEstablish = PlantingEstablish;
            SeedingAlgorithms SeedAlgorithm = (SeedingAlgorithms)Enum.Parse(typeof(SeedingAlgorithms), Names.parameters["SeedingAlgorithm"].Value);
            base.Initialize(ModelCore, SeedAlgorithm);
             
            

            PlugIn.ModelCore.UI.WriteLine("Spinning up biomass or reading from maps...");

            string InitialCommunitiesTXTFile = Names.GetParameter(Names.InitialCommunities).Value;
            string InitialCommunitiesMapFile = Names.GetParameter(Names.InitialCommunitiesMap).Value;
            InitialCommunitiesSpinup = Names.GetParameter(Names.InitialCommunitiesSpinup).Value;
            MinFolRatioFactor = ((Parameter<float>)Names.GetParameter(Names.MinFolRatioFactor,0,float.MaxValue)).Value;
            Parameter<string> LitterMapFile;
            bool litterMapFile = Names.TryGetParameter(Names.LitterMap, out LitterMapFile);
            Parameter<string> WoodyDebrisMapFile;
            bool woodyDebrisMapFile = Names.TryGetParameter(Names.WoodyDebrisMap, out WoodyDebrisMapFile);
            InitializeSites(InitialCommunitiesTXTFile, InitialCommunitiesMapFile, ModelCore);
            if(litterMapFile)
                MapReader.ReadLitterFromMap(LitterMapFile.Value);
            if(woodyDebrisMapFile)
                MapReader.ReadWoodyDebrisFromMap(WoodyDebrisMapFile.Value);

            // Convert PnET cohorts to biomasscohorts
            foreach (ActiveSite site in PlugIn.ModelCore.Landscape)
            {
                SiteVars.UniversalCohorts[site] = SiteVars.SiteCohorts[site];

                if (SiteVars.SiteCohorts[site] != null && SiteVars.UniversalCohorts[site] == null)
                {
                    throw new System.Exception("Cannot convert PnET SiteCohorts to biomass site cohorts");
                }
            }

            ModelCore.RegisterSiteVar(SiteVars.UniversalCohorts, "Succession.UniversalCohorts");
            ISiteVar<SiteCohorts> PnETCohorts = PlugIn.ModelCore.Landscape.NewSiteVar<SiteCohorts>();

            foreach (ActiveSite site in PlugIn.ModelCore.Landscape)
            {
                PnETCohorts[site] = SiteVars.SiteCohorts[site];
                SiteVars.FineFuels[site] = SiteVars.Litter[site].Mass;
                IEcoregionPnET ecoregion = EcoregionData.GetPnETEcoregion(PlugIn.ModelCore.Ecoregion[site]);
                IHydrology hydrology = new Hydrology(ecoregion.FieldCap);
                float currentPressureHead = hydrology.PressureHeadTable.CalculateWaterPressure(hydrology.Water, ecoregion.SoilType);
                SiteVars.PressureHead[site] = currentPressureHead;

                //PressureHead[site] = currentPressureHead;
                SiteVars.FieldCapacity[site] = ecoregion.FieldCap / 10.0F; // cm volume (accounts for rooting depth)

                if (UsingClimateLibrary)
                {
                    SiteVars.ExtremeMinTemp[site] = ((float)Climate.FutureEcoregionYearClimate[ecoregion.Index][1].MonthlyTemp.Min()
                        - (float)(3.0 * ecoregion.WinterSTD));

                    if (((Parameter<bool>)Names.GetParameter(Names.SoilIceDepth)).Value)
                    { 
                        if(SiteVars.MonthlySoilTemp[site].Count() == 0)
                        { 
                        // Soil calcs for soil temp
                        float waterContent = hydrology.Water;// volumetric m/m
                        float porosity = ecoregion.Porosity;  // volumetric m/m 
                        float ga = 0.035F + 0.298F * (waterContent / porosity);
                        float Fa = ((2.0F / 3.0F) / (1.0F + ga * ((Constants.lambda_a / Constants.lambda_w) - 1.0F))) + ((1.0F / 3.0F) / (1.0F + (1.0F - 2.0F * ga) * ((Constants.lambda_a / Constants.lambda_w) - 1.0F))); // ratio of air temp gradient
                        float Fs = PressureHeadSaxton_Rawls.GetFs(ecoregion.SoilType);
                        float lambda_s = PressureHeadSaxton_Rawls.GetLambda_s(ecoregion.SoilType);
                        float lambda_theta = (Fs * (1.0F - porosity) * lambda_s + Fa * (porosity - waterContent) * Constants.lambda_a + waterContent * Constants.lambda_w) / (Fs * (1.0F - porosity) + Fa * (porosity - waterContent) + waterContent); //soil thermal conductivity (kJ/m/d/K)
                        float D = lambda_theta / PressureHeadSaxton_Rawls.GetCTheta(ecoregion.SoilType);  //m2/day
                        float Dmms = D * 1000000 / 86400; //mm2/s
                        float d = (float)Math.Sqrt(2 * Dmms / Constants.omega);
                        float maxDepth = ecoregion.RootingDepth + ecoregion.LeakageFrostDepth;
                        float bottomFreezeDepth = maxDepth / 1000;

                            foreach (var year in Climate.SpinupEcoregionYearClimate[ecoregion.Index])
                            {
                                List<double> monthlyAirT = Climate.SpinupEcoregionYearClimate[ecoregion.Index][year.CalendarYear].MonthlyTemp;
                                double annualAirTemp = Climate.SpinupEcoregionYearClimate[ecoregion.Index][year.CalendarYear].MeanAnnualTemperature;
                                List<double> monthlyPrecip = Climate.SpinupEcoregionYearClimate[ecoregion.Index][year.CalendarYear].MonthlyPrecip;
                                SortedList<float, float> depthTempDict = new SortedList<float, float>();
                                SiteVars.MonthlyPressureHead[site] = new float [monthlyAirT.Count()];
                                SiteVars.MonthlySoilTemp[site] = new SortedList<float, float>[monthlyAirT.Count()];

                                for (int m = 0; m < monthlyAirT.Count(); m++)
                                {
                                    SiteVars.MonthlyPressureHead[site][m] = currentPressureHead;

                                    float DRz_snow = 1F; // Assume no snow in initialization

                                    float mossDepth = ecoregion.MossDepth;
                                    float cv = 2500; // heat capacity moss - kJ/m3/K (Sazonova and Romanovsky 2003)
                                    float lambda_moss = 432; // kJ/m/d/K - converted from 0.2 W/mK (Sazonova and Romanovsky 2003)
                                    float moss_diffusivity = lambda_moss / cv;
                                    float damping_moss = (float)Math.Sqrt((2.0F * moss_diffusivity) / Constants.omega);
                                    float DRz_moss = (float)Math.Exp(-1.0F * mossDepth * damping_moss); // Damping ratio for moss - adapted from Kang et al. (2000) and Liang et al. (2014)


                                    // Fill the tempDict with values
                                    float testDepth = 0;
                                    float zTemp = 0;
                                    int month = m + 1;
                                    int maxMonth = 0;
                                    int minMonth = 0;
                                    int mCount = 0;
                                    float tSum = 0;
                                    float pSum = 0;
                                    float tMax = float.MinValue;
                                    float tMin = float.MaxValue;

                                    if (m < 12)
                                    {
                                        mCount = Math.Min(12, monthlyAirT.Count());
                                        foreach (int z in Enumerable.Range(0, mCount))
                                        {
                                            tSum += (float)monthlyAirT[z];
                                            pSum += (float)monthlyPrecip[z];
                                            if (monthlyAirT[z] > tMax)
                                            {
                                                tMax = (float)monthlyAirT[z];
                                                maxMonth = z + 1;
                                            }
                                            if (monthlyAirT[z] < tMin)
                                            {
                                                tMin = (float)monthlyAirT[z];
                                                minMonth = z + 1;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        mCount = 12;
                                        foreach (int z in Enumerable.Range(m - 11, 12))
                                        {
                                            tSum += (float)monthlyAirT[z];
                                            pSum += (float)monthlyPrecip[z];
                                            if ((float)monthlyAirT[z] > tMax)
                                            {
                                                tMax = (float)monthlyAirT[z];
                                                maxMonth = month + z;
                                            }
                                            if ((float)monthlyAirT[z] < tMin)
                                            {
                                                tMin = (float)monthlyAirT[z];
                                                minMonth = month + z;
                                            }
                                        }
                                    }
                                    float annualTavg = tSum / mCount;
                                    float annualPcpAvg = pSum / mCount;
                                    float tAmplitude = (tMax - tMin) / 2;

                                    // Calculate depth to bottom of ice lens with FrostDepth
                                    while (testDepth <= bottomFreezeDepth)
                                    {
                                        float DRz = (float)Math.Exp(-1.0F * testDepth * d * ecoregion.FrostFactor); // adapted from Kang et al. (2000) and Liang et al. (2014); added FrostFactor for calibration
                                                                                                                    //float zTemp = annualTavg + (tempBelowSnow - annualTavg) * DRz;
                                                                                                                    // Calculate lag months from both max and min temperature months
                                        int lagMax = (month + (3 - maxMonth));
                                        int lagMin = (month + (minMonth - 5));
                                        if (minMonth >= 9)
                                            lagMin = (month + (minMonth - 12 - 5));
                                        float lagAvg = ((float)lagMax + (float)lagMin) / 2f;

                                        zTemp = (float)(annualAirTemp + tAmplitude * DRz_snow * DRz_moss * DRz * Math.Sin(Constants.omega * lagAvg - testDepth / d));
                                        depthTempDict[testDepth] = zTemp;

                                        if (testDepth == 0f)
                                            testDepth = 0.10f;
                                        else if (testDepth == 0.10f)
                                            testDepth = 0.25f;
                                        else
                                            testDepth += 0.25F;
                                    }
                                    SiteVars.MonthlySoilTemp[site][m] = Permafrost.CalculateMonthlySoilTemps(depthTempDict, ecoregion, 0, 0, hydrology, (float)monthlyAirT[m]);
                                }
                            }
                        }
                    }
                }
                else
                {
                    SiteVars.ExtremeMinTemp[site] = 999;
                }
            }
            PlugIn.ModelCore.RegisterSiteVar(PnETCohorts, "Succession.CohortsPnET");



        }
        /*
        private void ConvertToUniversalCohorts()
        {
            foreach (ActiveSite site in PlugIn.ModelCore.Landscape)
            {
                SiteVars.UniversalCohorts[site] = new Library.UniversalCohorts.SiteCohorts();

                foreach(Landis.Library.UniversalCohorts.ISpeciesCohorts speciesCohort in SiteVars.SiteCohorts[site])
                {
                    foreach (Landis.Library.UniversalCohorts.ICohort cohort in speciesCohort)
                    {
                        SiteVars.UniversalCohorts[site].AddNewCohort(cohort.Species, cohort.Data.Age, (int)cohort.Data.Biomass, 
                            cohort.Data.ANPP, cohort.Data.AdditionalParameters);
                    }
                }

                if (SiteVars.SiteCohorts[site] != null && SiteVars.UniversalCohorts[site] == null)
                {
                    throw new System.Exception("Cannot convert PnET SiteCohorts to biomass site cohorts");
                }
            }
        }
        */

        //---------------------------------------------------------------------
        /// <summary>This must be called after EcoregionPnET.Initialize() has been called</summary>
        private void InitializeClimateLibrary(int startYear = 0)
        {
            // John McNabb: initialize ClimateRegionData after initializing EcoregionPnet
            Parameter<string> climateLibraryFileName;
            UsingClimateLibrary = Names.TryGetParameter(Names.ClimateConfigFile, out climateLibraryFileName);
            if (UsingClimateLibrary)
            {
                PlugIn.ModelCore.UI.WriteLine($"Using climate library: {climateLibraryFileName.Value}.");
                Climate.Initialize(climateLibraryFileName.Value, false, ModelCore);
                ClimateRegionData.Initialize();
            }
            //else
            //{  
            //    PlugIn.ModelCore.UI.WriteLine($"Using climate files in ecoregion parameters: {Names.parameters["EcoregionParameters"].Value}.");
            //}

            string PARunits = ((Parameter<string>)Names.GetParameter(Names.PARunits)).Value;

            if (PARunits == "umol")
            {
                PlugIn.ModelCore.UI.WriteLine("Using PAR units of umol/m2/s.");
            }
            else if(PARunits == "W/m2")
            {
                PlugIn.ModelCore.UI.WriteLine("Using PAR units of W/m2.");
            }else
            {
                throw new ApplicationException(string.Format("PARunits units are not 'umol' or 'W/m2'"));
            }
        }
        //---------------------------------------------------------------------
        public void AddNewCohort(ISpecies species, ActiveSite site, string reproductionType, double propBiomass = 1.0)
        {
            ISpeciesPnET spc = PlugIn.SpeciesPnET[species];
            bool addCohort = true;
            if (SiteVars.SiteCohorts[site].cohorts.ContainsKey(species))
            {
                // This should deliver only one KeyValuePair
                KeyValuePair<ISpecies, List<Cohort>> i = new List<KeyValuePair<ISpecies, List<Cohort>>>(SiteVars.SiteCohorts[site].cohorts.Where(o => o.Key == species))[0];
                List<Cohort> Cohorts = new List<Cohort>(i.Value.Where(o => o.Age < CohortBinSize));
                if (Cohorts.Count() > 0)
                {
                    addCohort = false;
                }
            }
            bool addSiteOutput = false;
            addSiteOutput = (SiteOutputNames.ContainsKey(site) && addCohort);
            Cohort cohort = new Cohort(species, spc, (ushort)Date.Year, (addSiteOutput) ? SiteOutputNames[site] : null, propBiomass, false);
            if (((Parameter<bool>)Names.GetParameter(Names.CohortStacking)).Value)
            {
                cohort.CanopyGrowingSpace = 1.0f;
                cohort.CanopyLayerProp = 1.0f;
            }
            
            addCohort = SiteVars.SiteCohorts[site].AddNewCohort(cohort);

            if (addCohort)
            {
                if (reproductionType == "plant")
                {
                    if (!SiteVars.SiteCohorts[site].SpeciesEstablishedByPlant.Contains(species))
                        SiteVars.SiteCohorts[site].SpeciesEstablishedByPlant.Add(species);
                }
                else if (reproductionType == "serotiny")
                {
                    if (!SiteVars.SiteCohorts[site].SpeciesEstablishedBySerotiny.Contains(species))
                        SiteVars.SiteCohorts[site].SpeciesEstablishedBySerotiny.Add(species);
                }
                else if (reproductionType == "resprout")
                {
                    if (!SiteVars.SiteCohorts[site].SpeciesEstablishedByResprout.Contains(species))
                        SiteVars.SiteCohorts[site].SpeciesEstablishedByResprout.Add(species);
                }
                else if (reproductionType == "seed")
                {
                    if (!SiteVars.SiteCohorts[site].SpeciesEstablishedBySeed.Contains(species))
                        SiteVars.SiteCohorts[site].SpeciesEstablishedBySeed.Add(species);
                }

                // Recalculate BiomassLayerProp for layer 0 after adding new cohort?? Should only apply to biomass
            }
        }
        //---------------------------------------------------------------------
        public bool MaturePresent(ISpecies species, ActiveSite site)
        {
            bool IsMaturePresent = SiteVars.SiteCohorts[site].IsMaturePresent(species);
            return IsMaturePresent;
        }
        //---------------------------------------------------------------------
        protected override void InitializeSite(ActiveSite site)//,ICommunity initialCommunity)
        {
            lock (threadLock)
            {
                if (m == null)
                {
                    m = new MyClock(PlugIn.ModelCore.Landscape.ActiveSiteCount);
                }

                m.Next();
                m.WriteUpdate();
            }

            uint key = 0;
            allKeys.TryGetValue(site, out key);

            ICommunity initialCommunity = null;

            if (!sitesAndCommunities.TryGetValue(site, out initialCommunity))
            {
                throw new ApplicationException(string.Format("Unable to retrieve initialCommunity for site: {0}", site.Location.Row + "," + site.Location.Column));
            }

            if (!SiteCohorts.InitialSitesContainsKey(key))
            {
                // Create new sitecohorts from scratch
                SiteVars.SiteCohorts[site] = new SiteCohorts(StartDate, site, initialCommunity, UsingClimateLibrary, PlugIn.InitialCommunitiesSpinup, MinFolRatioFactor, SiteOutputNames.ContainsKey(site) ? SiteOutputNames[site] : null);
            }
            else
            {
                // Create new sitecohorts using initialcommunities data
                SiteVars.SiteCohorts[site] = new SiteCohorts(StartDate, site, initialCommunity, SiteOutputNames.ContainsKey(site) ? SiteOutputNames[site] : null);
            }
        }
        //---------------------------------------------------------------------
        public override void InitializeSites(string initialCommunitiesText, string initialCommunitiesMap, ICore modelCore)
        {
            ModelCore.UI.WriteLine("   Loading initial communities from file \"{0}\" ...", initialCommunitiesText);
            DatasetParser parser = new DatasetParser(Timestep, modelCore.Species, additionalCohortParameters, initialCommunitiesText);

            //Landis.Library.InitialCommunities.DatasetParser parser = new Landis.Library.InitialCommunities.DatasetParser(Timestep, ModelCore.Species);
            IDataset communities = Landis.Data.Load<IDataset>(initialCommunitiesText, parser);

            List<ActiveSite> processFirst = new List<ActiveSite>();
            List<ActiveSite> processSecond = new List<ActiveSite>();

            ModelCore.UI.WriteLine("   Reading initial communities map \"{0}\" ...", initialCommunitiesMap);
            ProcessInitialCommunitiesMap(initialCommunitiesMap, communities, ref processFirst, ref processSecond);

            if (this.ThreadCount != 1)
            {
                // Handle creation of initial community sites first
                Parallel.ForEach(processFirst, new ParallelOptions { MaxDegreeOfParallelism = this.ThreadCount }, site =>
                {
                    InitializeSite(site);
                });

                Parallel.ForEach(processSecond, new ParallelOptions { MaxDegreeOfParallelism = this.ThreadCount }, site =>
                {
                    InitializeSite(site);
                });
            }
            else
            {
                // First, process sites so that the initial communities are set up
                foreach (ActiveSite site in processFirst)
                {
                    InitializeSite(site);
                }

                foreach (ActiveSite site in processSecond)
                {
                    InitializeSite((ActiveSite)site);
                }
            }
        }
        //---------------------------------------------------------------------
        protected override void AgeCohorts(ActiveSite site,
                                            ushort years,
                                            int? successionTimestep)                                            
        {
            // Date starts at 1/15/Year
            DateTime date = new DateTime(PlugIn.StartDate.Year + PlugIn.ModelCore.CurrentTime - Timestep, 1, 15);

            DateTime EndDate = date.AddYears(years);

            IEcoregionPnET ecoregion_pnet = EcoregionData.GetPnETEcoregion(PlugIn.ModelCore.Ecoregion[site]);

            List<IEcoregionPnETVariables> climate_vars = UsingClimateLibrary ? EcoregionData.GetClimateRegionData(ecoregion_pnet, date, EndDate) : EcoregionData.GetData(ecoregion_pnet, date, EndDate);

            SiteVars.SiteCohorts[site].Grow(climate_vars);
            SiteVars.SiteCohorts[site].DisturbanceTypesReduced.Clear();

            Date = EndDate;
        }
        //---------------------------------------------------------------------
        // Required function - not used within PnET-Succession
        public override byte ComputeShade(ActiveSite site)
        {
            return 0;
        }
        //---------------------------------------------------------------------
        public override void Run()
        {
            if (Timestep > 0)
                ClimateRegionData.SetAllEcoregionsFutureAnnualClimate(ModelCore.CurrentTime);
            base.Run();
        }
        //---------------------------------------------------------------------
        // Does not seem to be used
        /*public void AddLittersAndCheckResprouting(object sender, Landis.Library.AgeOnlyCohorts.DeathEventArgs eventArgs)
        {
            if (eventArgs.DisturbanceType != null)
            {
                ActiveSite site = eventArgs.Site;
                Disturbed[site] = true;

                if (eventArgs.DisturbanceType.IsMemberOf("disturbance:fire"))
                    Reproduction.CheckForPostFireRegen(eventArgs.Cohort, site);
                else
                    Reproduction.CheckForResprouting(eventArgs.Cohort, site);
            }
        }*/
        //---------------------------------------------------------------------
        // This is a Delegate method to base succession.
        // Not used within PnET-Succession
        public bool SufficientResources(ISpecies species, ActiveSite site)
        {
            return true;
        }
        //---------------------------------------------------------------------
        /// <summary>
        /// Determines if a species can establish on a site.
        /// This is a Delegate method to base succession.
        /// </summary>
        public bool Establish(ISpecies species, ActiveSite site)
        {
            ISpeciesPnET spc = PlugIn.SpeciesPnET[species];

            bool Establish = SiteVars.SiteCohorts[site].EstablishmentProbability.HasEstablished(spc);
            return Establish;
        }
        //---------------------------------------------------------------------
        /// <summary>
        /// Determines if a species can be planted on a site (all conditions are satisfied).
        /// This is a Delegate method to base succession.
        /// </summary>
        public bool PlantingEstablish(ISpecies species, ActiveSite site)
        {
            return true;
        }
        //---------------------------------------------------------------------

        //---------------------------------------------------------------------
        /// <summary>
        /// Reads the initial communities map, finds all unique site keys, and sets aside sites to process first and second
        /// </summary>
        private void ProcessInitialCommunitiesMap(string initialCommunitiesMap, 
            IDataset communities, ref List<ActiveSite> processFirst,
            ref List<ActiveSite> processSecond)
        {
            IInputRaster<UIntPixel> map = ModelCore.OpenRaster<UIntPixel>(initialCommunitiesMap);
            Dictionary<uint, ActiveSite> uniqueKeys = new Dictionary<uint, ActiveSite>();

            using (map)
            {
                UIntPixel pixel = map.BufferPixel;
                foreach (Site site in ModelCore.Landscape.AllSites)
                {
                    map.ReadBufferPixel();
                    uint mapCode = pixel.MapCode.Value;
                    if (!site.IsActive)
                        continue;

                    ActiveSite activeSite = (ActiveSite)site;
                    var initialCommunity = communities.Find(mapCode);
                    if (initialCommunity == null)
                    {
                        throw new ApplicationException(string.Format("Unknown map code for initial community: {0}", mapCode));
                    }

                    sitesAndCommunities.Add(activeSite, initialCommunity);
                    uint key = SiteCohorts.ComputeKey((ushort)initialCommunity.MapCode, Globals.ModelCore.Ecoregion[site].MapCode);

                    if (!uniqueKeys.ContainsKey(key))
                    {
                        uniqueKeys.Add(key, activeSite);
                        processFirst.Add(activeSite);
                    }
                    else
                    {
                        processSecond.Add(activeSite);
                    }

                    if (!allKeys.ContainsKey(activeSite))
                    {
                        allKeys.Add(activeSite, key);
                    }
                }
            }
        }

        public override void AddCohortData()
        {
            // CUSTOM DYNAMIC PARAMETERS GO HERE
            return;
        }
        //---------------------------------------------------------------------
    }
}


