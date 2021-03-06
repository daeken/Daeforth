﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DaeForth;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PrettyPrinter;
using static DaeForth.MathFunctions;

namespace MarchMadness {
	class Program {
		static void Main(string[] args) {
			var references = AppDomain.CurrentDomain.GetAssemblies()
				.Select(x => MetadataReference.CreateFromFile(x.Location)).ToList();
			var compiler = new Compiler();
			compiler.Compile(args[0].Split('/')[^1], File.ReadAllText(args[0]));
			var code = compiler.GenerateCode(new CSharpBackend());
			Console.Error.WriteLine(code);
			var cst = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest));
			var optimize = true;
			var options = new CSharpCompilationOptions(
				OutputKind.DynamicallyLinkedLibrary,
				optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
				allowUnsafe: true);

			var lc = CSharpCompilation.Create("MarchMadnessCompilation", options: options,
				references: references);
			var compilation = lc.AddSyntaxTrees(cst);
			using var ms = new MemoryStream();
			var er = compilation.Emit(ms);
			foreach(var diag in er.Diagnostics)
				Console.Error.WriteLine(diag);
			if(!er.Success) return;
			
			var asm = AppDomain.CurrentDomain.Load(ms.ToArray());
			var type = asm.GetType("DFShader");
			var mi = type.GetMethod("map_scene_float_Vec3");
			var map = (Func<Vec3, float>) mi.CreateDelegate(typeof(Func<Vec3, float>));

			var tmin = new Vec3(-5.5f);
			var tmax = new Vec3(5.5f);
			/*var density = 0.01f;
			var maxResolution = 1000;
			var diff = tmax - tmin;
			var _totalResolution = diff / density;
			var totalSteps = (X: (int) MathF.Ceiling(_totalResolution.X / maxResolution),
				Y: (int) MathF.Ceiling(_totalResolution.Y / maxResolution),
				Z: (int) MathF.Ceiling(_totalResolution.Z / maxResolution));
			var step = diff / (_totalResolution / maxResolution);

			var pieces = Enumerable.Range(0, totalSteps.X).Select(x =>
				Enumerable.Range(0, totalSteps.Y)
					.Select(y => Enumerable.Range(0, totalSteps.Z).Select(z => new Vec3(x, y, z)))
					.SelectMany(x => x)).SelectMany(x => x).ToList();
			var verts = pieces.AsParallel().Select(v => {
					var cmin = step * v + tmin;
					var cmax = min(cmin + step, tmax);
					var cdiff = cmax - cmin;
					var cverts = MarchingCubes.March(cmin, cmax, map, 0,
						(int) MathF.Ceiling(cdiff.X / density),
						(int) MathF.Ceiling(cdiff.Y / density),
						(int) MathF.Ceiling(cdiff.Z / density));
					Console.WriteLine($"Generated {cverts.Count} at {v.ToPrettyString()}");
					return MeshSimplifier.Simplify(cverts);
				}).AsSequential().SelectMany(x => x).ToList();*/

			//var verts = LayerStacking.Build(map, tmin, tmax, (500, 500, 500));
			//var verts = OctreeLayerStacking.Build(map, tmin, tmax, 9);

			List<Vec3> DualContour() {
				List<int> Quads2Tris(List<int> input) {
					var output = new List<int>();
					for(var i = 0; i < input.Count; i += 4) {
						output.Add(input[i+0]);
						output.Add(input[i+1]);
						output.Add(input[i+2]);
				
						output.Add(input[i+0]);
						output.Add(input[i+2]);
						output.Add(input[i+3]);
					}

					return output;
				}
				
				DC._Program.Scene = map;
				DC._Program.SceneNormal = x => {
					var epsilon = 0.0001f;
					return new Vec3(
						map(x + new Vec3(epsilon, 0, 0)) - map(x - new Vec3(epsilon, 0, 0)),
						map(x + new Vec3(0, epsilon, 0)) - map(x - new Vec3(0, epsilon, 0)),
						map(x + new Vec3(0, 0, epsilon)) - map(x - new Vec3(0, 0, epsilon))
					).Normalized;
				};
				Console.WriteLine("Building octree");
				var root = DC.Octree.BuildOctree(tmin, tmax, 2048, 0);
				var tverts = new List<(Vec3, Vec3)>();
				var indices = new List<int>();
				Console.WriteLine("Generating mesh");
				DC.Octree.GenerateMeshFromOctree(root, tverts, indices);
				//var vertices = tverts.Select(x => x.Item1).ToList();
				//var normals = tverts.Select(x => x.Item1).ToList();
				return Quads2Tris(indices).Select(x => tverts[x].Item1).ToList();
			}

			var verts = DualContour();
			Console.WriteLine($"Generated {verts.Count} total vertices");
			verts = MeshSimplifier.Simplify(verts, verts.Count / 3 / 10);

			verts = verts.Select(x => x * 100).Select(x => new Vec3(x.X, -x.Z, x.Y)).ToList();
			
			Console.Error.WriteLine($"Writing {verts.Count} vertices");
			using var sw = new StreamWriter(args[1], false);
			sw.WriteLine("solid marched");
			for(var i = 0; i < verts.Count; i += 3) {
				var a = verts[i];
				var b = verts[i + 1];
				var c = verts[i + 2];
				sw.WriteLine("facet normal 0 0 0");
				sw.WriteLine("outer loop");
				sw.WriteLine($"vertex {a.X} {a.Y} {a.Z}");
				sw.WriteLine($"vertex {b.X} {b.Y} {b.Z}");
				sw.WriteLine($"vertex {c.X} {c.Y} {c.Z}");
				sw.WriteLine("endloop");
				sw.WriteLine("endfacet");
			}
			sw.WriteLine("endsolid marched");
		}
	}
}