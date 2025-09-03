// Copyright (c) 2023 bradson
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using HashCode = FisheryLib.HashCode;

namespace PerformanceFish.System;

public sealed class EqualityComparerOptimization : ClassWithFishPatches
{
	public sealed class Optimization : FishPatch
	{
		public override string? Description { get; }
			= "Specialized EqualityComparers for slightly faster collection lookups on ValueTypes. Can reduce memory "
			+ "usage by preventing unnecessary boxing into object and helps with performance of a few UI methods. "
			+ "Changes require a restart.";
		
		public override MethodInfo? TryPatch() => null;
		
		public static void Initialize()
		{
			if (!Get<Optimization>().Enabled)
				return;

			var types = AccessTools.AllTypes().ToList();

			Parallel.ForEach(Partitioner.Create(0, types.Count), (range, _) =>
			{
				for (var i = range.Item1; i < range.Item2; i++)
				{
					try
					{
						var type = types[i];
						object? equalityComparer                                            // check easy type - int, ushort, or string are *easy* - immediately invoke default comparers.
								= type == typeof(int) ? new IntEqualityComparer()
								: type == typeof(ushort) ? new UshortEqualityComparer()
								: type == typeof(string) ? new StringEqualityComparer()
								: null;

						if (equalityComparer != null)                                       // type is null -> 
						{
							SetValueForDefaultComparer(type, equalityComparer);             // run SetValueDefaultComparer... 
							return;
						}

						if (!type.IsValueType                                               // if the type is not a ValueType, is void, is Generic, is Enum, or is Byte 
							|| type == typeof(void)                                         // is void
							|| type.IsGenericTypeDefinition                                 // is a generic
							|| type.IsEnum                                                  // is an enum
							|| type == typeof(byte)                                         // is byte 
							|| type.IsByRef                                                 // is ref
							|| type.IsByRefLike                                             // is ref struct
							|| type.IsPointer)                                              // is pointer,  we skip this type
						{
							return;
						}

						SetValueForDefaultComparer(type,
							Activator.CreateInstance(
								type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>)
									? (type.GetGenericArguments()[0] is var genericArgument
										&& genericArgument.IsAssignableTo(typeof(IEquatable<>), type)
											? typeof(EquatableNullableEqualityComparer<>)
											: typeof(NullableEqualityComparer<>)).MakeGenericType(genericArgument)
									: (type.IsAssignableTo(typeof(IEquatable<>), type)
										? typeof(EquatableValueTypeEqualityComparer<>)
										: typeof(ValueTypeEqualityComparer<>)).MakeGenericType(type))); // exception code
					}
					catch (Exception e)
					{
						Log.Message($" Ran into recoverable {e.GetType().Name} exception within EqualityComparisonOptimizations: {e.Message}");
					}
				}
			});  

			// Parallel.ForEach(Partitioner.Create(0, types.Count), (range, _) =>			// partitioner creates "chunky" sets of works for parallel threads. 
			// {																			// in each chunk, do these: 
			// 	for (var i = range.Item1; i < range.Item2; i++)
			// 	{
			// 		var type = types[i];												// type = types[i]
			// 		object? equalityComparer											// check easy type - int, ushort, or string are *easy* - immediately invoke default comparers.
			// 			= type == typeof(int) ? new IntEqualityComparer()
			// 			: type == typeof(ushort) ? new UshortEqualityComparer()
			// 			: type == typeof(string) ? new StringEqualityComparer()
			// 			: null;
			// 
			// 		if (equalityComparer != null)										// type is null -> 
			// 		{
			// 			SetValueForDefaultComparer(type, equalityComparer);				// run SetValueDefaultComparer... 
			// 			return;
			// 		}
			// 	
			// 		if (!type.IsValueType												// if the type is not a ValueType, is void, is Generic, is Enum, or is Byte 
			// 			|| type == typeof(void)											// we want to skip this type;
			// 			|| type.IsGenericTypeDefinition
			// 			|| type.IsEnum
			// 			|| type == typeof(byte))
			// 		{
			// 			return;
			// 		}
			// 
			// 		SetValueForDefaultComparer(type,
			// 			Activator.CreateInstance(
			// 				type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>)
			// 					? (type.GetGenericArguments()[0] is var genericArgument
			// 						&& genericArgument.IsAssignableTo(typeof(IEquatable<>), type)
			// 							? typeof(EquatableNullableEqualityComparer<>)
			// 							: typeof(NullableEqualityComparer<>)).MakeGenericType(genericArgument)
			// 					: (type.IsAssignableTo(typeof(IEquatable<>), type)
			// 						? typeof(EquatableValueTypeEqualityComparer<>)
			// 						: typeof(ValueTypeEqualityComparer<>)).MakeGenericType(type)));
			// 	}
			// });
		}

		private static void SetValueForDefaultComparer(Type type, object value)
			=> typeof(EqualityComparer<>).MakeGenericType(type).GetField("defaultComparer", AccessTools.allDeclared)!
				.SetValue(null, value);
	}
}

[Serializable]
public sealed class EquatableValueTypeEqualityComparer<T> : EqualityComparer<T> where T : struct, IEquatable<T>
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(T x, T y) => x.Equals(y);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(T obj) => HashCode.Get(obj);

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(EquatableValueTypeEqualityComparer<T>);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class EquatableNullableEqualityComparer<T> : EqualityComparer<T?> where T : struct, IEquatable<T>
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(T? x, T? y)
		=> x.HasValue
			? y.HasValue && x.GetValueOrDefault().Equals(y.GetValueOrDefault())
			: !y.HasValue;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(T? obj) => !obj.HasValue ? 0 : HashCode.Get(obj.GetValueOrDefault());

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(EquatableNullableEqualityComparer<T>);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class NullableEqualityComparer<T> : EqualityComparer<T?> where T : struct
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(T? x, T? y)
		=> x.HasValue
			? y.HasValue && x.GetValueOrDefault().Equals<T>(y.GetValueOrDefault())
			: !y.HasValue;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(T? obj) => !obj.HasValue ? 0 : HashCode.Get(obj.GetValueOrDefault());

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(NullableEqualityComparer<T>);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class StringEqualityComparer : EqualityComparer<string?>
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	[SuppressMessage("Globalization", "CA1309")]
	public override bool Equals(string? x, string? y)
		=> string.Equals(x, y); // already does a null check, so this skips the extra check that'd otherwise happen

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(string? obj) => obj?.GetHashCode() ?? 0;

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(StringEqualityComparer);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class IntEqualityComparer : EqualityComparer<int>
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(int x, int y) => x == y;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(int obj) => obj;

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(IntEqualityComparer);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class UshortEqualityComparer : EqualityComparer<ushort>
{
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(ushort x, ushort y) => x == y;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(ushort obj) => obj;

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(UshortEqualityComparer);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class ReferenceEqualityComparer<T> : EqualityComparer<T>
{
	public new static readonly ReferenceEqualityComparer<T> Default = new();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(T x, T y) => ReferenceEquals(x, y);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(ReferenceEqualityComparer<T>);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}

[Serializable]
public sealed class ValueTypeEqualityComparer<T> : EqualityComparer<T> where T : struct
{
	public new static readonly ValueTypeEqualityComparer<T> Default = new();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override bool Equals(T x, T y) => x.Equals<T>(y);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public override int GetHashCode(T obj) => HashCode.Get(obj);

	/// <summary>
	/// Equals method for the comparer itself.
	/// </summary>
	public override bool Equals(object? obj) => obj?.GetType() == typeof(ReferenceEqualityComparer<T>);

	public override int GetHashCode() => GetType().Name.GetHashCode();
}
