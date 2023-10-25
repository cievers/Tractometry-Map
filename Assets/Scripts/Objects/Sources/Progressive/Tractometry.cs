﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Camera;
using Evaluation;
using Evaluation.Coloring;
using Evaluation.Geometric;
using Files;
using Files.Types;
using Geometry;
using Geometry.Generators;
using Geometry.Tracts;
using Interface.Content;
using Interface.Control;
using Logic.Eventful;
using Maps.Cells;
using Maps.Grids;
using Objects.Concurrent;
using UnityEngine;
using UnityEngine.Serialization;
using Boolean = Logic.Eventful.Boolean;
using Exporter = Interface.Control.Exporter;

namespace Objects.Sources.Progressive {
	public class Tractometry : Voxels {
		public MeshFilter tractogramMesh;
		public MeshFilter gridMesh;
		public MeshFilter coreMesh;
		public MeshFilter coreOutlineMesh;
		public MeshFilter spanMesh;
		public MeshFilter spanOutlineMesh;
		public MeshFilter cutMesh;
		public MeshFilter volumeMesh;
		public GameObject dot;

		private Tck tractogram;
		private ThreadedLattice grid;
		private new ThreadedRenderer renderer;
		private CrossSectionExtrema prominentCuts;
		private Tract core;
		private Summary summary;

		private TractogramRenderer wireRenderer;
		private TractogramRenderer outlineRenderer;

		private ConcurrentPipe<Tuple<Cell, Tract>> voxels;
		private ConcurrentBag<Dictionary<Cell, Vector>> measurements;
		private ConcurrentBag<Dictionary<Cell, Color32>> colors;
		private ConcurrentPipe<Model> maps;
		private ConcurrentPipe<float> voxelSurfaces;
		private ConcurrentPipe<float> voxelVolumes;

		private Thread quantizeThread;
		private Thread renderThread;
		
		private PromiseCollector<Tract> promisedCore;
		private PromiseCollector<Model> promisedCut;
		private PromiseCollector<Hull> promisedVolume;

		private Files.Exporter exportMap;
		private Files.Exporter exportCore;
		private Files.Exporter exportSummary;

		private Any loading;

		private int samples = 32;
		private float prominence = 0;
		private float resolution = 1;
		private int batch = 4096;
		private TractEvaluation evaluation;
		
		private Map map;
		private Dictionary<Cell, Vector> measurement;

		protected override void New(string path) {
			voxels = new ConcurrentPipe<Tuple<Cell, Tract>>();
			measurements = new ConcurrentBag<Dictionary<Cell, Vector>>();
			colors = new ConcurrentBag<Dictionary<Cell, Color32>>();
			maps = new ConcurrentPipe<Model>();
			summary = new Summary();

			wireRenderer = new WireframeRenderer();
			outlineRenderer = new TubeRenderer(16, 0.5f, (_, normal, _) => new Color(Math.Abs(normal.x), Math.Abs(normal.z), Math.Abs(normal.y)));

			promisedCore = new PromiseCollector<Tract>();
			promisedCut = new PromiseCollector<Model>();
			promisedVolume = new PromiseCollector<Hull>();

			exportMap = new Files.Exporter("Save as NIFTI", "nii", Nifti);
			exportCore = new Files.Exporter("Save as TCK", "tck", () => new SimpleTractogram(new[] {core}));
			exportSummary = new Files.Exporter("Save numeric summary", "json", summary.Json);

			tractogram = Tck.Load(path);
			evaluation = new TractEvaluation(new CompoundMetric(new TractMetric[] {new Density()}), new Grayscale());
			
			loading = new Any(new Boolean[] {maps, promisedCore, promisedCut, promisedVolume});
			loading.Change += state => Loading(!state);

			UpdateSamples();
			UpdateTracts();
			UpdateMap();
			Focus(new Focus(grid.Boundaries.Center, grid.Boundaries.Size.magnitude / 2 * 1.5f));
		}

		private void Update() {
			if (measurements.TryTake(out var measured)) {
				measurement = measured;
			}
			if (colors.TryTake(out var colored)) {
				map = new Map(colored, grid.Cells, grid.Size, grid.Boundaries);
				Configure(grid.Cells, colored, grid.Size, grid.Boundaries);
			}
			if (maps.TryTake(out var result)) {
				gridMesh.mesh = result.Mesh();
				
				// A bit of a hack checking for this here, as these evaluations also make some sense before the entire map is done
				if (maps.IsCompleted) {
					UpdateMapEvaluation();
				}
			}

			if (promisedCore.TryTake(out var tract)) {
				core = tract;
				var span = new ArrayTract(new[] {tract.Points[0], tract.Points[^1]});
				
				coreMesh.mesh = wireRenderer.Render(tract);
				spanMesh.mesh = wireRenderer.Render(span);
				coreOutlineMesh.mesh = outlineRenderer.Render(tract);
				spanOutlineMesh.mesh = outlineRenderer.Render(span);
			}
			if (promisedCut.TryTake(out var cuts)) {
				cutMesh.mesh = cuts.Mesh();
			}
			if (promisedVolume.TryTake(out var hull)) {
				volumeMesh.mesh = hull.Mesh();
			}
		}
		private void UpdateTracts() {
			tractogramMesh.mesh = new WireframeRenderer().Render(tractogram);
		}
		private void UpdateSamples() {
			var sampler = new Resample(tractogram, samples);
			var core = new Core(sampler);
			var cut = new CrossSection(sampler, core);
			var volume = new Volume(sampler, cut);

			prominentCuts = new CrossSectionExtrema(cut, prominence);

			promisedCore.Add(core);
			promisedVolume.Add(volume);
			core.Request(summary.Core);
			cut.Request(summary.CrossSections);
			// cut.Request(cuts => core.Request(tract => summary.CrossSectionsVolume(tract, cuts)));
			UpdateCutEvaluation();
		}
		private void UpdateSamples(int samples) {
			this.samples = samples;
			UpdateSamples();
		}
		private void UpdateSamples(float samples) {
			UpdateSamples((int) Math.Round(samples));
		}
		private void UpdateCutProminence(float prominence) {
			this.prominence = prominence;
			prominentCuts.UpdateProminence(prominence);
			UpdateCutEvaluation();
		}
		private void UpdateCutEvaluation() {
			promisedCut.Add(new CrossSectionEvaluation(prominentCuts, evaluation.Coloring));
		}
		private void UpdateEvaluation(TractEvaluation evaluation) {
			this.evaluation = evaluation;
			// UpdateMap();
			renderer.Evaluate(evaluation);
		}
		private void UpdateResolution(float resolution) {
			this.resolution = resolution;
			UpdateMap();
		}
		private void UpdateBatch(int batch) {
			this.batch = batch;
			renderer.Batch(batch);
		}
		private void UpdateBatch(float batch) {
			UpdateBatch((int) Math.Round(batch));
		}
		private void UpdateMap() {
			voxels.Restart();
			maps.Restart();
			grid = new ThreadedLattice(tractogram, resolution, voxels);
			renderer = new ThreadedRenderer(voxels, measurements, colors, maps, grid, evaluation, batch);

			quantizeThread?.Abort();
			renderThread?.Abort();
			quantizeThread = new Thread(grid.Start);
			renderThread = new Thread(renderer.Render);
			quantizeThread.Start();
			renderThread.Start();
		}
		private void UpdateMapEvaluation() {
			new VoxelSurface(grid.Cells, grid.Size, grid.Resolution).Request(surface => summary.VoxelSurface = surface);
			new VoxelVolume(grid.Cells, grid.Resolution).Request(volume => summary.VoxelVolume = volume);
			UpdateCutEvaluation();
		}

		public override Map Map() {
			return map;
		}
		
		public override IEnumerable<Interface.Component> Controls() {
			return new Interface.Component[] {
				new ActionToggle.Data("Tracts", true, tractogramMesh.gameObject.SetActive),
				new Divider.Data(),
				new Folder.Data("Global measuring", new List<Interface.Component> {
					new TransformedSlider.Exponential("Resample count", 2, 5, 1, 8, (_, transformed) => ((int) Math.Round(transformed)).ToString(), new ValueChangeBuffer<float>(0.1f, UpdateSamples).Request),
					new Loader.Data(promisedCore, new ActionToggle.Data("Mean", true, state => {
							coreMesh.gameObject.SetActive(state);
							coreOutlineMesh.gameObject.SetActive(state);
					})),
					new Loader.Data(promisedCore, new ActionToggle.Data("Span", false, state => {
						spanMesh.gameObject.SetActive(state);
						spanOutlineMesh.gameObject.SetActive(state);
					})),
					new Loader.Data(promisedCut, new ActionToggle.Data("Cross-section", false, cutMesh.gameObject.SetActive)),
					new TransformedSlider.Data("Cross-section prominence", 0, value => value, (_, transformed) => ((int) Math.Round(transformed * 100)).ToString() + '%', new ValueChangeBuffer<float>(0.1f, UpdateCutProminence).Request),
					new Loader.Data(promisedVolume, new ActionToggle.Data("Volume", false, volumeMesh.gameObject.SetActive))
				}),
				new Divider.Data(),
				new Folder.Data("Local measuring", new List<Interface.Component> {
					new Loader.Data(maps, new ActionToggle.Data("Map", true, gridMesh.gameObject.SetActive)),
					new TransformedSlider.Exponential("Resolution", 10, 0, -1, 1, new ValueChangeBuffer<float>(0.1f, UpdateResolution).Request),
					new TransformedSlider.Exponential("Batch size", 2, 12, 1, 20, (_, transformed) => ((int) Math.Round(transformed)).ToString(), UpdateBatch),
				}),
				new Divider.Data(),
				new Folder.Data("Evaluation", new List<Interface.Component> {
					new Interface.Control.Evaluation.Data(UpdateEvaluation)
				}),
				new Divider.Data(),
				new Folder.Data("Exporting", new List<Interface.Component> {
					new Exporter.Data("Numerical summary", exportSummary),
					new Exporter.Data("Core tract", exportCore),
					new Exporter.Data("Map", exportMap)
				}),
			};
		}
		public override IEnumerable<Files.Publisher> Exports() {
			return new Publisher[] {exportMap, exportCore, exportSummary};
		}
		
		private Nii<float> Nifti() {
			// TODO: This uses only the grid's properties, so the grid should probably know by itself how to perform this formatting
			return new Nii<float>(ToArray(grid.Cells, measurement, 0), grid.Size, grid.Boundaries.Min + new Vector3(grid.Resolution / 2, grid.Resolution / 2, grid.Resolution / 2), new Vector3(grid.Resolution, grid.Resolution, grid.Resolution));
		}
		private T[] ToArray<T>(IReadOnlyList<Cuboid?> cells, IReadOnlyDictionary<Cell, T> values, T fill) {
			var result = new T[cells.Count];
			for (var i = 0; i < cells.Count; i++) {
				if (cells[i] != null && values.ContainsKey(cells[i])) {
					result[i] = values[cells[i]];
				} else {
					result[i] = fill;
				}
			}
			return result;
		}
		private float[] ToArray(IReadOnlyList<Cuboid?> cells, IReadOnlyDictionary<Cell, Vector> values, float fill) {
			var dimensions = values.Values.Select(vector => vector.Dimensions).Min();
			var result = new float[cells.Count * dimensions];
			for (var i = 0; i < cells.Count; i++) {
				if (cells[i] != null && values.ContainsKey(cells[i])) {
					for (var j = 0; j < dimensions; j++) {
						result[i * dimensions + j] = values[cells[i]][j];
					}
				} else {
					for (var j = 0; j < dimensions; j++) {
						result[i * dimensions + j] = fill;
					}
				}
			}
			return result;
		}
	}
}