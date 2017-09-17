﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using dndbg.COM.CorDebug;
using dndbg.Engine;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Engine.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Debugger.DotNet.CorDebug.Impl.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;

namespace dnSpy.Debugger.DotNet.CorDebug.Impl {
	abstract partial class DbgEngineImpl {
		internal DbgDotNetValue CreateDotNetValue_CorDebug(CorValue value, DmdAppDomain reflectionAppDomain, bool tryCreateStrongHandle) {
			debuggerThread.VerifyAccess();
			if (value == null)
				throw new ArgumentNullException(nameof(value));

			var type = new ReflectionTypeCreator(this, reflectionAppDomain).Create(value.ExactType);

			//TODO: You should support the by-ref case too
			if (tryCreateStrongHandle && !value.IsNull && !value.IsHandle && value.IsReference && !type.IsPointer && !type.IsFunctionPointer && !type.IsByRef) {
				var strongHandle = value.DereferencedValue?.CreateHandle(CorDebugHandleType.HANDLE_STRONG);
				Debug.Assert(strongHandle != null || type == type.AppDomain.System_TypedReference);
				if (strongHandle != null)
					value = strongHandle;
			}

			var dnValue = new DbgDotNetValueImpl(this, value, type);
			lock (lockObj)
				dotNetValuesToCloseOnContinue.Add(dnValue);
			return dnValue;
		}

		void CloseDotNetValues_CorDebug() {
			debuggerThread.VerifyAccess();
			DbgDotNetValueImpl[] valuesToClose;
			lock (lockObj) {
				valuesToClose = dotNetValuesToCloseOnContinue.Count == 0 ? Array.Empty<DbgDotNetValueImpl>() : dotNetValuesToCloseOnContinue.ToArray();
				dotNetValuesToCloseOnContinue.Clear();
			}
			foreach (var value in valuesToClose)
				value.Dispose_CorDebug();
		}

		internal void DisposeHandle_CorDebug(CorValue value) {
			Debug.Assert(debuggerThread.CheckAccess());
			dnDebugger.DisposeHandle(value);
		}

		CorType GetType(CorAppDomain appDomain, DmdType type) => CorDebugTypeCreator.GetType(this, appDomain, type);

		sealed class EvalTimedOut { }

		internal DbgDotNetValueResult? CheckFuncEval(DbgEvaluationContext context) {
			debuggerThread.VerifyAccess();
			if (dnDebugger.ProcessState != DebuggerProcessState.Paused)
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.CanFuncEvalOnlyWhenPaused);
			if (isUnhandledException)
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.CantFuncEvalWhenUnhandledExceptionHasOccurred);
			if (context.Session.HasData<EvalTimedOut>())
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOutNowDisabled);
			if (dnDebugger.IsEvaluating)
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.CantFuncEval);
			return null;
		}

		internal DbgDotNetValueResult FuncEvalCall_CorDebug(DbgEvaluationContext context, DbgThread thread, CorAppDomain appDomain, DmdMethodBase method, DbgDotNetValue obj, object[] arguments, bool newObj, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			var tmp = CheckFuncEval(context);
			if (tmp != null)
				return tmp.Value;
			Debug.Assert(!newObj || method.IsConstructor);

			if (method.SpecialMethodKind != DmdSpecialMethodKind.Metadata) {
				//TODO:
				return new DbgDotNetValueResult(CordbgErrorHelper.InternalError);
			}

			var reflectionAppDomain = thread.AppDomain.GetReflectionAppDomain() ?? throw new InvalidOperationException();
			var methodDbgModule = method.Module.GetDebuggerModule() ?? throw new InvalidOperationException();
			if (!TryGetDnModule(methodDbgModule, out var methodModule))
				return new DbgDotNetValueResult(CordbgErrorHelper.InternalError);
			var func = methodModule.CorModule.GetFunctionFromToken((uint)method.MetadataToken) ?? throw new InvalidOperationException();

			var dnThread = GetThread(thread);
			var createdValues = new List<CorValue>();
			try {
				using (var dnEval = dnDebugger.CreateEval()) {
					dnEval.SetThread(dnThread);
					dnEval.SetTimeout(context.FuncEvalTimeout);
					dnEval.EvalEvent += (s, e) => DnEval_EvalEvent(dnEval, context);

					var converter = new EvalArgumentConverter(this, dnEval, appDomain, reflectionAppDomain, createdValues);

					var genTypeArgs = method.ReflectedType.GetGenericArguments();
					var methTypeArgs = method.GetGenericArguments();
					var typeArgs = genTypeArgs.Count == 0 && methTypeArgs.Count == 0 ? Array.Empty<CorType>() : new CorType[genTypeArgs.Count + methTypeArgs.Count];
					int w = 0;
					for (int i = 0; i < genTypeArgs.Count; i++)
						typeArgs[w++] = GetType(appDomain, genTypeArgs[i]);
					for (int i = 0; i < methTypeArgs.Count; i++)
						typeArgs[w++] = GetType(appDomain, methTypeArgs[i]);
					if (typeArgs.Length != w)
						throw new InvalidOperationException();

					var paramTypes = GetAllMethodParameterTypes(method.GetMethodSignature());
					if (paramTypes.Count != arguments.Length)
						throw new InvalidOperationException();

					bool hiddenThisArg = !method.IsStatic && !newObj;
					int argsCount = arguments.Length + (hiddenThisArg ? 1 : 0);
					var args = argsCount == 0 ? Array.Empty<CorValue>() : new CorValue[argsCount];
					w = 0;
					DmdType origType;
					if (hiddenThisArg) {
						var val = converter.Convert(obj, method.DeclaringType, out origType);
						if (val.ErrorMessage != null)
							return new DbgDotNetValueResult(val.ErrorMessage);
						args[w++] = BoxIfNeeded(dnEval, createdValues, val.CorValue, method.DeclaringType, method.ReflectedType);
					}
					for (int i = 0; i < arguments.Length; i++) {
						var paramType = paramTypes[i];
						var val = converter.Convert(arguments[i], paramType, out origType);
						if (val.ErrorMessage != null)
							return new DbgDotNetValueResult(val.ErrorMessage);
						var valType = origType ?? new ReflectionTypeCreator(this, method.AppDomain).Create(val.CorValue.ExactType);
						args[w++] = BoxIfNeeded(dnEval, createdValues, val.CorValue, paramType, valType);
					}
					if (args.Length != w)
						throw new InvalidOperationException();

					// Derefence/unbox the values here now that they can't get neutered
					w = hiddenThisArg ? 1 : 0;
					for (int i = 0; i < arguments.Length; i++) {
						var paramType = paramTypes[i];
						var arg = args[w];
						if (paramType.IsValueType) {
							if (arg.IsReference) {
								if (arg.IsNull)
									throw new InvalidOperationException();
								arg = arg.DereferencedValue ?? throw new InvalidOperationException();
							}
							if (arg.IsBox)
								arg = arg.BoxedValue ?? throw new InvalidOperationException();
							args[w] = arg;
						}
						w++;
					}
					if (args.Length != w)
						throw new InvalidOperationException();

					var res = newObj ?
						dnEval.CallConstructor(func, typeArgs, args, out int hr) :
						dnEval.Call(func, typeArgs, args, out hr);
					if (res == null)
						return new DbgDotNetValueResult(CordbgErrorHelper.GetErrorMessage(hr));
					return new DbgDotNetValueResult(CreateDotNetValue_CorDebug(res.Value.ResultOrException, reflectionAppDomain, tryCreateStrongHandle: true), valueIsException: res.Value.WasException);
				}
			}
			catch (TimeoutException) {
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOut);
			}
			catch (Exception) {
				return new DbgDotNetValueResult(CordbgErrorHelper.InternalError);
			}
			finally {
				foreach (var value in createdValues)
					dnDebugger.DisposeHandle(value);
			}
		}

		static CorValue BoxIfNeeded(DnEval dnEval, List<CorValue> createdValues, CorValue corValue, DmdType targetType, DmdType valueType) {
			if (!targetType.IsValueType && valueType.IsValueType && corValue.IsGeneric && !corValue.IsHeap) {
				var boxedValue = dnEval.Box(corValue) ?? throw new InvalidOperationException();
				if (boxedValue != corValue)
					createdValues.Add(corValue);
				corValue = boxedValue;
			}
			return corValue;
		}

		static IList<DmdType> GetAllMethodParameterTypes(DmdMethodSignature sig) {
			if (sig.GetVarArgsParameterTypes().Count == 0)
				return sig.GetParameterTypes();
			var list = new List<DmdType>(sig.GetParameterTypes().Count + sig.GetVarArgsParameterTypes().Count);
			list.AddRange(sig.GetParameterTypes());
			list.AddRange(sig.GetVarArgsParameterTypes());
			return list;
		}

		internal DbgDotNetValueResult FuncEvalCreateInstanceNoCtor_CorDebug(DbgEvaluationContext context, DbgThread thread, CorAppDomain appDomain, DmdType typeToCreate, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			var tmp = CheckFuncEval(context);
			if (tmp != null)
				return tmp.Value;

			var dnThread = GetThread(thread);
			try {
				using (var dnEval = dnDebugger.CreateEval()) {
					dnEval.SetThread(dnThread);
					dnEval.SetTimeout(context.FuncEvalTimeout);
					dnEval.EvalEvent += (s, e) => DnEval_EvalEvent(dnEval, context);

					var corType = GetType(appDomain, typeToCreate);
					var res = dnEval.CreateDontCallConstructor(corType, out int hr);
					if (res == null)
						return new DbgDotNetValueResult(CordbgErrorHelper.GetErrorMessage(hr));
					return new DbgDotNetValueResult(CreateDotNetValue_CorDebug(res.Value.ResultOrException, typeToCreate.AppDomain, tryCreateStrongHandle: true), valueIsException: res.Value.WasException);
				}
			}
			catch (TimeoutException) {
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOut);
			}
			catch (Exception) {
				return new DbgDotNetValueResult(CordbgErrorHelper.InternalError);
			}
		}

		void DnEval_EvalEvent(DnEval dnEval, DbgEvaluationContext context) {
			if (dnEval.EvalTimedOut)
				context.Session.GetOrCreateData<EvalTimedOut>();
			dnDebugger.SignalEvalComplete();
		}
	}
}