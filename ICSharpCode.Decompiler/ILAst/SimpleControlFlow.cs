﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under MIT X11 license (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Mono.Cecil;

namespace ICSharpCode.Decompiler.ILAst
{
	public class SimpleControlFlow
	{
		Dictionary<ILLabel, int> labelGlobalRefCount = new Dictionary<ILLabel, int>();
		Dictionary<ILLabel, ILBasicBlock> labelToBasicBlock = new Dictionary<ILLabel, ILBasicBlock>();
		
		DecompilerContext context;
		TypeSystem typeSystem;
		
		public SimpleControlFlow(DecompilerContext context, ILBlock method)
		{
			this.context = context;
			this.typeSystem = context.CurrentMethod.Module.TypeSystem;
			
			foreach(ILLabel target in method.GetSelfAndChildrenRecursive<ILExpression>(e => e.IsBranch()).SelectMany(e => e.GetBranchTargets())) {
				labelGlobalRefCount[target] = labelGlobalRefCount.GetOrDefault(target) + 1;
			}
			foreach(ILBasicBlock bb in method.GetSelfAndChildrenRecursive<ILBasicBlock>()) {
				foreach(ILLabel label in bb.GetChildren().OfType<ILLabel>()) {
					labelToBasicBlock[label] = bb;
				}
			}
		}
		
		public bool SimplifyTernaryOperator(List<ILNode> body, ILBasicBlock head, int pos)
		{
			Debug.Assert(body.Contains(head));
			
			ILExpression condExpr;
			ILLabel trueLabel;
			ILLabel falseLabel;
			ILVariable trueLocVar = null;
			ILExpression trueExpr;
			ILLabel trueFall;
			ILVariable falseLocVar = null;
			ILExpression falseExpr;
			ILLabel falseFall;
			object unused;
			
			if (head.MatchLast(ILCode.Brtrue, out trueLabel, out condExpr, out falseLabel) &&
			    labelGlobalRefCount[trueLabel] == 1 &&
			    labelGlobalRefCount[falseLabel] == 1 &&
			    ((labelToBasicBlock[trueLabel].MatchSingle(ILCode.Stloc, out trueLocVar, out trueExpr, out trueFall) &&
			      labelToBasicBlock[falseLabel].MatchSingle(ILCode.Stloc, out falseLocVar, out falseExpr, out falseFall)) ||
			     (labelToBasicBlock[trueLabel].MatchSingle(ILCode.Ret, out unused, out trueExpr, out trueFall) &&
			      labelToBasicBlock[falseLabel].MatchSingle(ILCode.Ret, out unused, out falseExpr, out falseFall))) &&
			    trueLocVar == falseLocVar &&
			    trueFall == falseFall &&
			    body.Contains(labelToBasicBlock[trueLabel]) &&
			    body.Contains(labelToBasicBlock[falseLabel])
			   )
			{
				bool isStloc = trueLocVar != null;
				ILCode opCode = isStloc ? ILCode.Stloc : ILCode.Ret;
				TypeReference retType = isStloc ? trueLocVar.Type : this.context.CurrentMethod.ReturnType;
				int leftBoolVal;
				int rightBoolVal;
				ILExpression newExpr;
				// a ? true:false  is equivalent to  a
				// a ? false:true  is equivalent to  !a
				// a ? true : b    is equivalent to  a || b
				// a ? b : true    is equivalent to  !a || b
				// a ? b : false   is equivalent to  a && b
				// a ? false : b   is equivalent to  !a && b
				if (retType == typeSystem.Boolean &&
				    trueExpr.Match(ILCode.Ldc_I4, out leftBoolVal) &&
				    falseExpr.Match(ILCode.Ldc_I4, out rightBoolVal) &&
				    ((leftBoolVal != 0 && rightBoolVal == 0) || (leftBoolVal == 0 && rightBoolVal != 0))
				   )
				{
					// It can be expressed as trivilal expression
					if (leftBoolVal != 0) {
						newExpr = condExpr;
					} else {
						newExpr = new ILExpression(ILCode.LogicNot, null, condExpr);
					}
				} else if (retType == typeSystem.Boolean && trueExpr.Match(ILCode.Ldc_I4, out leftBoolVal)) {
					// It can be expressed as logical expression
					if (leftBoolVal != 0) {
						newExpr = MakeLeftAssociativeShortCircuit(ILCode.LogicOr, condExpr, falseExpr);
					} else {
						newExpr = MakeLeftAssociativeShortCircuit(ILCode.LogicAnd, new ILExpression(ILCode.LogicNot, null, condExpr), falseExpr);
					}
				} else if (retType == typeSystem.Boolean && falseExpr.Match(ILCode.Ldc_I4, out rightBoolVal)) {
					// It can be expressed as logical expression
					if (rightBoolVal != 0) {
						newExpr = MakeLeftAssociativeShortCircuit(ILCode.LogicOr, new ILExpression(ILCode.LogicNot, null, condExpr), trueExpr);
					} else {
						newExpr = MakeLeftAssociativeShortCircuit(ILCode.LogicAnd, condExpr, trueExpr);
					}
				} else {
					// Ternary operator tends to create long complicated return statements					
					if (opCode == ILCode.Ret)
						return false;
					
					// Only simplify generated variables
					if (opCode == ILCode.Stloc && !trueLocVar.IsGenerated)
						return false;
					
					// Create ternary expression
					newExpr = new ILExpression(ILCode.TernaryOp, null, condExpr, trueExpr, falseExpr);
				}
				
				head.Body[head.Body.Count - 1] = new ILExpression(opCode, trueLocVar, newExpr);
				head.FallthoughGoto = isStloc ? new ILExpression(ILCode.Br, trueFall) : null;
				
				// Remove the old basic blocks
				body.RemoveOrThrow(labelToBasicBlock[trueLabel]);
				body.RemoveOrThrow(labelToBasicBlock[falseLabel]);
				
				return true;
			}
			return false;
		}
		
		public bool SimplifyNullCoalescing(List<ILNode> body, ILBasicBlock head, int pos)
		{
			// ...
			// v = ldloc(leftVar)
			// brtrue(endBBLabel, ldloc(leftVar))
			// br(rightBBLabel)
			//
			// rightBBLabel:
			// v = rightExpr
			// br(endBBLabel)
			// ...
			// =>
			// ...
			// v = NullCoalescing(ldloc(leftVar), rightExpr)
			// br(endBBLabel)
				
			ILVariable v, v2;
			ILExpression leftExpr, leftExpr2;
			ILVariable leftVar;
			ILLabel endBBLabel, endBBLabel2;
			ILLabel rightBBLabel;
			ILBasicBlock rightBB;
			ILExpression rightExpr;
			if (head.Body.Count >= 2 &&
				head.Body[head.Body.Count - 2].Match(ILCode.Stloc, out v, out leftExpr) &&
			    leftExpr.Match(ILCode.Ldloc, out leftVar) &&
			    head.MatchLast(ILCode.Brtrue, out endBBLabel, out leftExpr2, out rightBBLabel) &&
			    leftExpr2.Match(ILCode.Ldloc, leftVar) &&
			    labelToBasicBlock.TryGetValue(rightBBLabel, out rightBB) &&
			    rightBB.MatchSingle(ILCode.Stloc, out v2, out rightExpr, out endBBLabel2) &&
			    v == v2 &&
			    endBBLabel == endBBLabel2 &&
			    labelGlobalRefCount.GetOrDefault(rightBBLabel) == 1 &&
			    body.Contains(rightBB)
			   )
			{
				head.Body[head.Body.Count - 2] = new ILExpression(ILCode.Stloc, v, new ILExpression(ILCode.NullCoalescing, null, leftExpr, rightExpr));
				head.Body.RemoveAt(head.Body.Count - 1);
				head.FallthoughGoto = new ILExpression(ILCode.Br, endBBLabel);
				
				body.RemoveOrThrow(labelToBasicBlock[rightBBLabel]);
				return true;
			}
			return false;
		}
		
		public bool SimplifyShortCircuit(List<ILNode> body, ILBasicBlock head, int pos)
		{
			Debug.Assert(body.Contains(head));
			
			ILExpression condExpr;
			ILLabel trueLabel;
			ILLabel falseLabel;
			if(head.MatchLast(ILCode.Brtrue, out trueLabel, out condExpr, out falseLabel)) {
				for (int pass = 0; pass < 2; pass++) {
					
					// On the second pass, swap labels and negate expression of the first branch
					// It is slightly ugly, but much better then copy-pasting this whole block
					ILLabel nextLabel   = (pass == 0) ? trueLabel  : falseLabel;
					ILLabel otherLablel = (pass == 0) ? falseLabel : trueLabel;
					bool    negate      = (pass == 1);
					
					ILBasicBlock nextBasicBlock = labelToBasicBlock[nextLabel];
					ILExpression nextCondExpr;
					ILLabel nextTrueLablel;
					ILLabel nextFalseLabel;
					if (body.Contains(nextBasicBlock) &&
					    nextBasicBlock != head &&
					    labelGlobalRefCount[nextBasicBlock.EntryLabel] == 1 &&
					    nextBasicBlock.MatchSingle(ILCode.Brtrue, out nextTrueLablel, out nextCondExpr, out nextFalseLabel) &&
					    (otherLablel == nextFalseLabel || otherLablel == nextTrueLablel))
					{
						// Create short cicuit branch
						ILExpression logicExpr;
						if (otherLablel == nextFalseLabel) {
							logicExpr = MakeLeftAssociativeShortCircuit(ILCode.LogicAnd, negate ? new ILExpression(ILCode.LogicNot, null, condExpr) : condExpr, nextCondExpr);
						} else {
							logicExpr = MakeLeftAssociativeShortCircuit(ILCode.LogicOr, negate ? condExpr : new ILExpression(ILCode.LogicNot, null, condExpr), nextCondExpr);
						}
						head.Body[head.Body.Count - 1] = new ILExpression(ILCode.Brtrue, nextTrueLablel, logicExpr);
						head.FallthoughGoto = new ILExpression(ILCode.Br, nextFalseLabel);
						
						// Remove the inlined branch from scope
						body.RemoveOrThrow(nextBasicBlock);
						
						return true;
					}
				}
			}
			return false;
		}
		
		ILExpression MakeLeftAssociativeShortCircuit(ILCode code, ILExpression left, ILExpression right)
		{
			// Assuming that the inputs are already left associative
			if (right.Match(code)) {
				// Find the leftmost logical expression
				ILExpression current = right;
				while(current.Arguments[0].Match(code))
					current = current.Arguments[0];
				current.Arguments[0] = new ILExpression(code, null, left, current.Arguments[0]);
				return right;
			} else {
				return new ILExpression(code, null, left, right);
			}
		}
		
		public bool JoinBasicBlocks(List<ILNode> body, ILBasicBlock head, int pos)
		{
			ILLabel nextLabel;
			ILBasicBlock nextBB;
			ILNode last = head.Body.LastOrDefault();
			if (!last.IsConditionalControlFlow() && 
			    !last.IsUnconditionalControlFlow() &&
			    head.FallthoughGoto.Match(ILCode.Br, out nextLabel) &&
			    labelGlobalRefCount[nextLabel] == 1 &&
			    labelToBasicBlock.TryGetValue(nextLabel, out nextBB) &&
			    body.Contains(nextBB) &&
			    nextBB.EntryLabel == nextLabel &&
			    !nextBB.Body.OfType<ILTryCatchBlock>().Any()
			   )
			{
				head.Body.AddRange(nextBB.Body);
				head.FallthoughGoto = nextBB.FallthoughGoto;
				
				body.RemoveOrThrow(nextBB);
				return true;
			}
			return false;
		}
	}
}