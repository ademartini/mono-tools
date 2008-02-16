//
// Gendarme.Rules.Performance.AvoidUninstantiatedInternalClassesRule
//
// Authors:
//	Nidhi Rawal <sonu2404@gmail.com>
//	Sebastien Pouliot <sebastien@ximian.com>
//
// Copyright (c) <2007> Nidhi Rawal
// Copyright (C) 2007-2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;

using Mono.Cecil;
using Mono.Cecil.Cil;

using Gendarme.Framework;
using Gendarme.Framework.Rocks;

namespace Gendarme.Rules.Performance {

	[Problem ("The internal type is not instantiated by code within the assembly.")]
	[Solution ("Remove the type or add the code that uses it.  If the type contains only static methods then either add the static modifier to the type or add the private construtor to the type to prevent the compiler from emitting a default public instance constructor.")]
	public class AvoidUninstantiatedInternalClassesRule : Rule, ITypeRule {

		private static bool CheckSpecialTypes (TypeDefinition type)
		{
			// <Module> isn't tagged as generated by the compiler but still must be ignored
			if (type.Name == Constants.ModuleType)
				return true;

			// some stuff, like arrays, is nested under <PrivateImplementationDetails>
			return type.IsGeneratedCode ();
		}

		// we use this to cache the information about the assembly
		// i.e. all types instantiated by the assembly
		private Dictionary<AssemblyDefinition, List<TypeReference>> cache;

		public void CacheInstantiationFromAssembly (AssemblyDefinition assembly)
		{
			if (cache == null)
				cache = new Dictionary<AssemblyDefinition, List<TypeReference>> ();
			else if (cache.ContainsKey (assembly))
				return;

			List<TypeReference> list = new List<TypeReference> ();

			foreach (ModuleDefinition module in assembly.Modules) {
				foreach (TypeDefinition type in module.Types) {
					ProcessType (type, list);
				}
			}

			cache.Add (assembly, list);
		}

		private static void AddType (List<TypeReference> list, TypeReference type)
		{
			// we're interested in the array element type, not the array itself
			if (type.IsArray ())
				type = type.GetOriginalType ();

			// only keep stuff from this assembly, which means we have a TypeDefinition (not a TypeReference)
			// and types that are not visible outside the assembly (since this is what we check for)
			TypeDefinition td = (type as TypeDefinition);
			if ((td != null) && !td.IsVisible ())
				list.AddIfNew (type);
		}

		public static void ProcessType (TypeDefinition type, List<TypeReference> list)
		{
			foreach (FieldDefinition field in type.Fields) {
				TypeReference t = field.FieldType;
				// don't add the type itself (e.g. enums)
				if (type != t)
					AddType (list, t);
			}
			foreach (MethodDefinition method in type.Constructors) {
				ProcessMethod (method, list);
			}
			foreach (MethodDefinition method in type.Methods) {
				ProcessMethod (method, list);
			}
		}

		public static void ProcessMethod (MethodDefinition method, List<TypeReference> list)
		{
			// this is needed in case we return an enum, a struct or something mapped 
			// to p/invoke (i.e. no ctor called). We also need to check for arrays.
			TypeReference t = method.ReturnType.ReturnType;
			AddType (list, t);

			// an "out" from a p/invoke must be flagged
			foreach (ParameterDefinition parameter in method.Parameters) {
				// we don't want the reference (&) on the type
				t = parameter.ParameterType.GetOriginalType ();
				AddType (list, t);
			}

			if (!method.HasBody)
				return;

			// add every type of variables we use
			foreach (VariableDefinition variable in method.Body.Variables) {
				t = variable.VariableType;
				AddType (list, t);
			}

			// add every type we create or refer to (e.g. loading fields from an enum)
			foreach (Instruction ins in method.Body.Instructions) {
				if (ins.Operand == null)
					continue;

				t = ins.Operand as TypeReference;
				if (t == null) {
					// this covers MethodReference and FieldReference
					MemberReference m = ins.Operand as MemberReference;
					if (m != null)
						t = m.DeclaringType;
				}

				if (t != null)
					AddType (list, t);
			}
		}

		public RuleResult CheckType (TypeDefinition type)
		{
			// rule apply to internal (non-visible) types
			// note: IsAbstract also excludes static types (2.0)
			if (type.IsVisible () || type.IsAbstract || CheckSpecialTypes (type) || type.Module.Assembly.HasAttribute ("System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
				return RuleResult.DoesNotApply;

			// rule applies

			// if the type holds the Main entry point then it is considered useful
			MethodDefinition entry_point = type.Module.Assembly.EntryPoint;
			if ((entry_point != null) && (entry_point.DeclaringType == type))
				return RuleResult.Success;

			// create a cache of all type instantiation inside this 
			AssemblyDefinition assembly = type.Module.Assembly;
			CacheInstantiationFromAssembly (assembly);

			List<TypeReference> list = null;
			if (cache.ContainsKey (assembly))
				list = cache [assembly];

			// if we can't find the non-public type being used in the assembly then the rule fails
			if (list == null || !list.Contains (type)) {
				Runner.Report (type, Severity.High, Confidence.Normal, "There is no call for any of the types constructor found");
				return RuleResult.Failure;
			}
			return RuleResult.Success;
		}
	}
}
