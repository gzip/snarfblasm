using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Romulus;
using System.Diagnostics;

namespace snarfblasm
{
    class ExpressionEvaluator
    {
        /// <summary>The storage of named values.</summary>
        IValueNamespace ValueNamespace;
        public ExpressionEvaluator(IValueNamespace valueNamespace) {
            this.ValueNamespace = valueNamespace;
            ErrorOnUndefinedSymbols = true;
            //this.IntermediateOverflowChecking = true;
        }

        public bool SignedMode { get; set; }
        public bool OverflowChecking { get; set; }
        ///// <summary>If true, overflow checking will be performed at each step of the computation, provided that OverflowChecking is also true.</summary>
        //public bool IntermediateOverflowChecking { get; set; }
        //public string MostRecentNamedLabel { get; set; } // Used to evaluate local labels (e.g. @looptop)
        public NameGetter MostRecentNamedLabelGetter { get; set; } // Used to evaluate local labels (e.g. @looptop)
        
        public delegate string StringGetter();
        public delegate Identifier NameGetter();

        /// <summary>
        /// If true, an error will occur when undefined symbols are referenced. If false, all undefined values will return a 16-bit value of zero.
        /// </summary>
        public bool ErrorOnUndefinedSymbols { get; set; }

        Stack<EvaluationStackFrame> EvaluationStack = new Stack<EvaluationStackFrame>();
        bool EvaluationInProgress = false;



        /// <summary>
        /// Evaluates an expression and returns an 8- or 16-bit integer.
        /// </summary>
        /// <param name="expression">The expression to evaluate.</param>
        /// <param name="sourceLine">The line the expression occurs on (used for exceptions)</param>
        /// <returns></returns>
        public LiteralValue EvaluateExpression(ref StringSection expression, int sourceLine, out Error error) {
            return EvaluateExpression(ref expression, sourceLine, evalMode.TopLevel, out error);
        }
        private LiteralValue EvaluateExpression(ref StringSection expression, int sourceLine, evalMode expType, out Error error) {
            if (expression.Trim().Length == 0) {
                error = new Error(ErrorCode.Expected_Expression, Error.Msg_ExpectedValue, sourceLine);
                return default(LiteralValue);
            }

            // If we re-enter, we need to save the existing state to get it back later
            if (EvaluationInProgress)
                PushEvaluationFrame();

            EvaluationInProgress = true;
            ExpressionType = expType;
            this.sourceLine = sourceLine;
            binary_ParseStack.Clear();


            var result = EvaluateInternal(ref expression, out error);
            if (error.IsError) error = new Error(error, sourceLine);

            // When done, we either restore the previous evaluation state if there is one.
            if (EvaluationStack.Count > 0) {
                PullEvaluationStackFrame();
            } else {
                EvaluationInProgress = false;
            }


            return result;
        }


        private void PullEvaluationStackFrame() {
            var frame = EvaluationStack.Pop();

            this.unary_PreOpIndexList = frame.unary_PreOpIndexList;
            this.unary_PostOpIndexList = frame.unary_PostOpIndexList;
            this.binary_ParseStack = frame.binary_ParseStack;
            this.sourceLine = frame.sourceLine;
            this.ExpressionType = frame.ExpressionType;
        }

        private void PushEvaluationFrame() {
            EvaluationStackFrame frame;

            // Push frame
            frame.unary_PreOpIndexList = this.unary_PreOpIndexList;
            frame.unary_PostOpIndexList = this.unary_PostOpIndexList;
            frame.binary_ParseStack = this.binary_ParseStack;
            frame.sourceLine = this.sourceLine;
            frame.ExpressionType = this.ExpressionType;

            EvaluationStack.Push(frame);

            // Reset variables
            this.unary_PreOpIndexList = new List<int>();
            this.unary_PostOpIndexList = new List<int>();
            this.binary_ParseStack = new List<ParseStackItem>();
        }


        List<ParseStackItem> binary_ParseStack = new List<ParseStackItem>();

        List<int> unary_PreOpIndexList = new List<int>();
        List<int> unary_PostOpIndexList = new List<int>();

        evalMode ExpressionType;
        int sourceLine;



        private LiteralValue EvaluateInternal(ref StringSection exp, out Error error) {
            LiteralValue anonymousLabelResult;
            if (TryParseAnonymousLabel(ref exp, out anonymousLabelResult, out error) || error.Code != ErrorCode.None) {
                return anonymousLabelResult;
            }


            bool runParseLoop = true;

            while (runParseLoop) {

                // Find next value and operator
                ParseStackItem stackItem;

                stackItem.Value = ParseUnary(ref exp, out error);
                if (error.Code != ErrorCode.None)
                    return default(LiteralValue);

                ParseBinaryOperator(ref exp, out stackItem.OperatorLevel, out stackItem.OperatorIndex, out error);
                bool operatorFound = stackItem.OperatorLevel != -1;
                if (!operatorFound)
                    runParseLoop = false;

                binary_ParseStack.Add(stackItem);
                
                // Evaluate any operations we can
                bool runEvalLoop = true;
                while (runEvalLoop) {
                    bool canCombine = binary_ParseStack.Count > 1 && binary_ParseStack[binary_ParseStack.Count - 1].OperatorLevel <= binary_ParseStack[binary_ParseStack.Count - 2].OperatorLevel;
                    if (canCombine) {
                        ParseStackItem combined = CombineParseStackItems(binary_ParseStack[binary_ParseStack.Count - 2], binary_ParseStack[binary_ParseStack.Count - 1], out error);

                        if (error.IsError) {
                            return default(LiteralValue);
                        }

                        // Pop two items and replace with combined item
                        binary_ParseStack.RemoveAt(binary_ParseStack.Count - 1);
                        binary_ParseStack.RemoveAt(binary_ParseStack.Count - 1);
                        binary_ParseStack.Add(combined);
                    } else {
                        runEvalLoop = false;
                    }
                }
            }

            exp = exp.TrimLeft();

            // Todo: remove this function, but make sure we do verify somewhere that the entire expression has been parsed.
            //VerifyExpressionEnd(ref exp, out error);

            // We should end up with a single stack item with an operator level of -1, and containing our return value
            if (binary_ParseStack.Count == 1 && binary_ParseStack[0].OperatorLevel == -1) {
                error = Error.None;
                var result = binary_ParseStack[0].Value;
                binary_ParseStack.Clear();
                return result;
            } else {
                Debug.Fail("Unexpected error.");
                error = new Error(ErrorCode.Engine_Error, "An error occurred in the expression parser.");
                return default(LiteralValue);
            }
        }

        private bool TryParseAnonymousLabel(ref StringSection exp, out LiteralValue result, out Error error) {
            error = Error.None;
            result = default(LiteralValue);

            // Test for '+' label
            int i = 0;
            while (i < exp.Length && exp[i] == '+') {
                i++;
            }

            if (i > 0 && i == exp.Length) { // expression was all '+'s
                exp = StringSection.Empty;

                int address = ValueNamespace.GetForwardLabel(i, sourceLine);
                if (address == -1)
                    error = new Error(ErrorCode.Anonymous_Label_Not_Found, Error.Msg_AnonLabelNotFound, sourceLine);
                else
                    result = new LiteralValue((ushort)address, false);

                return true;
            }

            // Test for - labels
            i = 0;
            while (i < exp.Length && exp[i] == '-') {
                i++;
            }

            if (i == exp.Length) { // expression was all '-'s
                exp = StringSection.Empty;

                int address = ValueNamespace.GetBackwardLabel(i, sourceLine);
                if (address == -1)
                    error = new Error(ErrorCode.Anonymous_Label_Not_Found, Error.Msg_AnonLabelNotFound, sourceLine);
                else
                    result = new LiteralValue((ushort)address, false);

                return true;
            }

            // Test for '}' label
            i = 0;
            while (i < exp.Length && exp[i] == '}') {
                i++;
            }

            if (i == exp.Length) { // expression was all '}'s
                exp = StringSection.Empty;

                int address = ValueNamespace.GetForwardBrace(i, sourceLine);
                if (address == -1)
                    error = new Error(ErrorCode.Anonymous_Label_Not_Found, Error.Msg_AnonLabelNotFound, sourceLine);
                else
                    result = new LiteralValue((ushort)address, false);

                return true;
            }

            // Test for { labels
            i = 0;
            while (i < exp.Length && exp[i] == '{') {
                i++;
            }

            if (i == exp.Length) { // expression was all '{'s
                exp = StringSection.Empty;

                int address = ValueNamespace.GetBackwardBrace(i, sourceLine);
                if (address == -1)
                    error = new Error(ErrorCode.Anonymous_Label_Not_Found, Error.Msg_AnonLabelNotFound, sourceLine);
                else
                    result = new LiteralValue((ushort)address, false);

                return true;
            }

            return false;
        }

        private void VerifyExpressionEnd(ref StringSection exp, out Error error) {
            error = Error.None;

            switch (ExpressionType) {
                case evalMode.TopLevel:
                    return;
                case evalMode.Paren:
                    if (exp[0] == ')') {
                        exp = exp.Substring(1);
                    } else {
                        error = new Error(ErrorCode.Expected_Closing_Paren, "Expected: \"(\"");
                    }
                    break;
                default:
                    Debug.Fail("Invalid case.");
                    error = new Error(ErrorCode.Engine_Error, "Engine error.");
                    break;
            }
        }

        enum evalMode
        {
            /// <summary>
            /// Top level evaluation, i.e. not in parens. This isn't necessarily at the top of the stack, but rather top-level within an expression (See remarks).
            /// </summary>
            /// <remarks>
            /// If the caller is asked to resolve a symbol, it could result in another expression evaluation. This would result in multiple top-level frames on the stack.
            /// </remarks>
            TopLevel,
            /// <summary>
            /// The expression being evaluated is parenthetical.
            /// </summary>
            Paren
        }
       

        private ParseStackItem CombineParseStackItems(ParseStackItem a, ParseStackItem b, out Error error) {
            error = Error.None;
            
            var combiningOperator = BinaryExpressionOperator.Operators[a.OperatorLevel][a.OperatorIndex];

            bool aIsSigned =SignedMode && (combiningOperator.SignedOperands == SignedOperatonMode.Full || combiningOperator.SignedOperands == SignedOperatonMode.LHand);
            bool bIsSigned =SignedMode && ( combiningOperator.SignedOperands == SignedOperatonMode.Full || combiningOperator.SignedOperands == SignedOperatonMode.RHand );
            int aValue = GetIntegerValue(a.Value, aIsSigned);
            int bValue = GetIntegerValue(b.Value, bIsSigned);

            int resultValue = combiningOperator.Operation(aValue, bValue);
            bool resultIsByte = GetBinaryIsbyte(combiningOperator.ResultType, a.Value.IsByte, b.Value.IsByte);

            OverflowCheck(resultValue, resultIsByte, ref error);

            ParseStackItem newItem;
            var value = MakeLiteral(resultValue, resultIsByte);
            if(value == null){
                error = new Error(ErrorCode.Overflow, Error.Msg_ValueOutOfRange);
                return default(ParseStackItem);
            }
            newItem.Value = value.Value;
            newItem.OperatorIndex = b.OperatorIndex;
            newItem.OperatorLevel = b.OperatorLevel;

            return newItem;
        }

        void OverflowCheck(int value, bool isByte, ref Error error) {
            if(this.OverflowChecking) {
                if(this.SignedMode) {
                    if (isByte) {
                        if (value < -128 || value > 0x7F) error = new Error(ErrorCode.Overflow, Error.Msg_SByteOutOfRange); 
                    } else {
                        if (value < -32768 || value > 0x7FFF) error = new Error(ErrorCode.Overflow, Error.Msg_SWordOutOfRange); 
                    }
                } else { // (unsigned)
                    if (isByte) {
                        if (value < 0 || value > 0xFF) error = new Error(ErrorCode.Overflow, Error.Msg_SByteOutOfRange);
                    } else {
                        if (value < 0 || value > 0xFFFF) error = new Error(ErrorCode.Overflow, Error.Msg_UByteOutOfRange);
                    }

                }
            }
        }

        private bool GetBinaryIsbyte(ExpressionResultType expressionResultType, bool aIsByte, bool bIsByte) {
            switch (expressionResultType) {
                case ExpressionResultType.Widest:
                    // Result is byte only if both operands are bytes
                    return aIsByte & bIsByte;
                case ExpressionResultType.LHand:
                    return aIsByte;
                case ExpressionResultType.RHand:
                    return bIsByte;
                case ExpressionResultType.Byte:
                    return true;
                case ExpressionResultType.Word:
                    return false;
                default:
                    Debug.Fail("Unexpected error.");
                    throw new Exception(); // Todo: handlable, specific exception. NEED AN 'ENGINE EXCEPTION' OF SORTS to indicate a problem occurred that may not be caused by ASM code
            }
        }

        private void ParseBinaryOperator(ref StringSection exp, out int operatorLevel, out int operatorIndex, out Error error) {
            string opSymbol = GrabBinaryOperator(ref exp);
            if (opSymbol == null) {
                operatorLevel = -1;
                operatorIndex = -1;
                error = Error.None;
                return;
            }

            for (int iLevel = 0; iLevel < BinaryExpressionOperator.Operators.Length; iLevel++) {
                var operators = BinaryExpressionOperator.Operators[iLevel];

                for (int iOperator = 0; iOperator < operators.Length; iOperator++) {
                    if (string.Equals(opSymbol, operators[iOperator].Symbol)) {
                        operatorLevel = iLevel;
                        operatorIndex = iOperator;
                        error = Error.None;
                        return;
                    }
                }
            }

            Debug.Fail("A symbol was identified, but it's operator could not be found.");
            throw new Exception("Unexpected error."); // Todo: parse exception, so it can at least be handled.
        }

        /// <summary>
        /// Returns the binary operator at the beginning of the string, or null if none is found
        /// </summary>
        private string GrabBinaryOperator(ref StringSection exp) {
            if (exp.Length == 0) return null;

            for (int i = 0; i < BinaryExpressionOperator.OperatorSymbols.Length; i++) {
                string symbol = BinaryExpressionOperator.OperatorSymbols[i];
                
                bool match = false;

                bool longEnough = exp.Length >= symbol.Length;
                if (longEnough) {
                    match = true; // Assume match until we find the string doesn't match
                   
                    for (int iChar = 0; iChar < symbol.Length; iChar++) {
                        if (exp[iChar] != symbol[iChar]) {
                            match = false;
                            break;
                        }
                    }
                }

                if (match) {
                    exp = exp.Substring(symbol.Length).TrimLeft();
                    return symbol;
                }
            }

            return null;
        }

        /// <summary>
        /// Represents one item in the stack used to manage operator precedence.
        /// </summary>
        struct ParseStackItem
        {
            public LiteralValue Value;
            /// <summary>The precedence value of the operator. Higher values indicate higher precedence.</summary>
            public int OperatorLevel;
            public int OperatorIndex;
        }



        LiteralValue ParseUnary(ref StringSection exp, out Error error) {
            LiteralValue RValue;
            Identifier LValue;

            // Find pre-operators (e.g. ~value, !value, <value, ++value)
            bool search_PreOperator = true;
            while (search_PreOperator) {
                int operatorIndex = GetUnaryPreOp(ref exp);

                if (operatorIndex == -1) {
                    search_PreOperator = false; 
                } else {
                    unary_PreOpIndexList.Add(operatorIndex);
                }
            }

            GetValue(ref exp, out LValue, out RValue, out error);
            if (error.Code != ErrorCode.None) return default(LiteralValue);

            // Find post-operators (i.e. value++ and value--)
            bool search_PostOperator = true;
            while (search_PostOperator) {
                int operatorIndex = GetUnaryPostOp(ref exp);

                if (operatorIndex == -1) {
                    search_PostOperator = false;
                } else {
                    unary_PostOpIndexList.Add(operatorIndex);
                }
            }

            for (int i = 0; i < unary_PostOpIndexList.Count; i++) {
                int operationIndex = unary_PostOpIndexList[i];

                // Result of post-operations are discarded (they are used purely for side-effects)
                LiteralValue result = PerformUnaryPostOperation(operationIndex, LValue, RValue, out error);

                // As of yet, all operators produce R-Values
                LValue = Identifier.Empty;

                if (error.Code != ErrorCode.None) 
                    return default(LiteralValue);
            }
            unary_PostOpIndexList.Clear();

            // Perform pre-operators from rightmost to leftmost
            for (int i = unary_PreOpIndexList.Count - 1; i >= 0; i--) {
                int operationIndex = unary_PreOpIndexList[i];
                RValue = PerformUnaryPreOperation(operationIndex, LValue, RValue, out error);

                if (error.Code != ErrorCode.None)
                    return default(LiteralValue);
            }
            unary_PreOpIndexList.Clear();

            return RValue;
        }


        /// <summary>Returns the result of the operation, and performs any side-effects.</summary>
        private LiteralValue PerformUnaryPostOperation(int operationIndex, Identifier LValue, LiteralValue RValue, out Error error) {
            return PerformUnaryOperation_Internal(operationIndex, LValue, RValue, out error, UnaryExpressionOperator.PostOperators);
        }
        /// <summary>Returns the result of the operation, and performs any side-effects.</summary>
        private LiteralValue PerformUnaryPreOperation(int operationIndex, Identifier LValue, LiteralValue RValue, out Error error) {
            return PerformUnaryOperation_Internal(operationIndex, LValue, RValue, out error, UnaryExpressionOperator.PreOperators);
        }

        /// <summary>Returns the result of the operation, and performs any side-effects. A valid R-value must be specified, even if a corresponding L-value is specified.</summary>
        private LiteralValue PerformUnaryOperation_Internal(int operationIndex, Identifier LValue, LiteralValue RValue, out Error error, UnaryExpressionOperator[] operatorList) {
            var operation = operatorList[operationIndex];

            int operandValue = GetIntegerValue(RValue, operation.Signed);
            
            // Get result
            int intresult = operation.Operation(operandValue);
            bool IsByte = GetUnaryIsbyte(operation.ResultType, RValue.IsByte);
            var result = new LiteralValue((ushort)intresult, IsByte);
            
            // Check result
            if (OverflowChecking && operation.CanOverflow) {
                if (PerformOverflowCheck(intresult, IsByte)) {
                    error = new Error(ErrorCode.Overflow, "Expression resulted in an overflow.");
                    return default(LiteralValue);
                }
            }

            // Side effects
            if (operation.OperationType == UnaryOperation.Modifing) {
                if (LValue == null) {
                    error = new Error(ErrorCode.Expected_LValue, "Operator requires an assignable operand.");
                    return default(LiteralValue);
                }
                SetSymbolValue(LValue, result, out error);
                if (error.IsError) return result;
            }

            // Tada!
            error = Error.None;
            return result;
        }

        private bool PerformOverflowCheck(int value, bool IsByte) {
            if (IsByte) {
                if (SignedMode) {
                    return value < sbyte.MinValue | value > sbyte.MaxValue;
                } else {
                    return value < byte.MinValue | value > byte.MaxValue;
                }
            } else {
                if (SignedMode) {
                    return value < short.MinValue | value > short.MaxValue;
                } else {
                    return value < ushort.MinValue | value > ushort.MaxValue;
                }
            }
        }

        /// <summary>Identifies whether a given operator and operand produce a byte or word result.</summary>
        private bool GetUnaryIsbyte(ExpressionResultType resultType, bool operandIsByte) {
            switch (resultType) {
                case ExpressionResultType.Widest:
                case ExpressionResultType.LHand:
                case ExpressionResultType.RHand:
                    return operandIsByte;
                case ExpressionResultType.Byte:
                    return true;
                case ExpressionResultType.Word:
                    return false;
                default:
                    throw new ArgumentException("Invalid result type.");
            }
        }


        private int GetIntegerValue(LiteralValue value, bool allowSigned) {
            if (SignedMode & allowSigned) {
                if (value.IsByte) {
                    return (int)(sbyte)(byte)(value.Value); // (pointless)(signed)(truncate)
                } else {
                    return (int)(short)(value.Value); // (pointless)(signed)
                }
            } else {
                if (value.IsByte)
                    return value.Value & 0xFF;
                return value.Value;
            }
        }


        private void GetValue(ref StringSection exp, out Identifier LValue, out LiteralValue RValue, out Error error) {
            error = Error.None;
            LValue = Identifier.Empty;
            if (exp.IsNullOrEmpty) {
                error = new Error(ErrorCode.End_Of_Text, Error.Msg_ExpectedValue);
                RValue = default(LiteralValue);
                return;
            }

            // All valid conditions must return to avoid 'default' condition of 'unrecognized text' error

            if (exp[0] == '(') {
                RValue = EvaluateParentheses(ref exp, out error);
                return;
            } else if (exp[0] == '#') {
                exp = exp.Substring(1); // Remove #
                if (exp[0] == '$') {
                    // This can be a hex number ($12AB, or the current address: simply $)
                    exp = exp.Substring(1); // Remove $

                    StringSection hexString = GetHexString(exp);
                    exp = exp.Substring(hexString.Length).TrimLeft(); // Remove number
                    RValue = ParseHex(hexString, out error);
                    return;
                } else if (exp[0] == '%') {
                    exp = exp.Substring(1); // Remove %

                    StringSection binString = GetBinString(exp);
                    if (binString.Length == 0)
                        goto GOTO_InvalidNumber;
                    exp = exp.Substring(binString.Length).TrimLeft(); // Remove number
                    RValue = ParseBin(binString, out error);
                    return;
                } else if (char.IsDigit(exp[0])) {
                    StringSection decString = GetDecString(exp);
                    if (decString.Length == 0)
                        goto GOTO_InvalidNumber;
                    exp = exp.Substring(decString.Length).TrimLeft(); // Remove number
                    RValue = ParseDec(decString, out error);
                    return;
                }
            }else if (exp[0] == '$') {
                // This can be a hex number ($12AB, or the current address: simply $)
                exp = exp.Substring(1); // Remove $


                StringSection hexString = GetHexString(exp);
                if (hexString.Length == 0) { // If the $ is not followed by a valid hex number, we'll assume it is the $ variable
                    exp = exp.TrimLeft();
                    LValue = Identifier.CurrentInstruction;
                    RValue = ValueNamespace.GetValue(Identifier.CurrentInstruction);
                    return;
                }
                exp = exp.Substring(hexString.Length).TrimLeft(); // Remove number
                RValue = ParseHex(hexString, out error);
                return;
            } else if (exp[0] == '%') {
                exp = exp.Substring(1); // Remove %
                StringSection binString = GetBinString(exp);
                if (binString.Length == 0)
                    goto GOTO_InvalidNumber;
                exp = exp.Substring(binString.Length).TrimLeft(); // Remove number
                RValue = ParseBin(binString, out error);
                return;
            } else if (char.IsDigit(exp[0])) {
                StringSection decString = GetDecString(exp);
                if (decString.Length == 0)
                    goto GOTO_InvalidNumber;
                exp = exp.Substring(decString.Length).TrimLeft(); // Remove number
                RValue = ParseDec(decString, out error);
                return;
            }else if(exp[0] == '@'){ // local label
                if (exp.Length >= 2 && char.IsLetter(exp[1])) {
                    exp = exp.Substring(1);
                    var labelName = ParseSymbol(ref exp);
                    Identifier recent = MostRecentNamedLabelGetter();
                    labelName = new Identifier(recent.name + "." + labelName, recent.nspace); // Todo: this is gonna need to be a namespaced label (getter)

                    // Get label value
                    var rVal = GetSymbolValue(labelName);

                    if (rVal == null) {
                        // If it's not defined, throw an error
                        error = new Error(ErrorCode.Value_Not_Defined, string.Format(Error.Msg_ValueNotDefined_Name, labelName));
                        RValue = default(LiteralValue);
                        return;
                    } else {
                        // Otherwise, return the label value
                        RValue = rVal.Value;
                    }
                    // An error will occur if the code tries to re-assign a label value
                    LValue = labelName;

                    return;
                }
            } else if (char.IsLetter(exp[0]) || exp[0]=='_') {
                Identifier varName = ParseSymbol(ref exp);
                if (!varName.IsEmpty) {
                    var rVal = GetSymbolValue(varName);
                    if (rVal == null) {
                        error = new Error(ErrorCode.Value_Not_Defined, string.Format(Error.Msg_ValueNotDefined_Name,varName));
                        RValue = default(LiteralValue);
                        return;
                    }else{
                        RValue = rVal.Value;
                    }
                    LValue = varName;
                    return;
                }
            }

            RValue = default(LiteralValue);
            error = new Error(ErrorCode.Unexpected_Text, Error.Msg_ExpectedValue);
            return;

        GOTO_InvalidNumber:
            RValue = default(LiteralValue);
            error = new Error(ErrorCode.Invalid_Number, "Invalid number");
            return;
        }

        private LiteralValue EvaluateParentheses(ref StringSection exp, out Error error) {
            // Get rid of opening paren
            exp = exp.Substring(1);

            // Find closing paren
            int indexOfClosingParen = FindIndexOfClosingParen(exp);
            if (indexOfClosingParen == -1) {
                error = new Error(ErrorCode.Syntax_Error, Error.Msg_MissingCloseParen, sourceLine);
            }

            // Split paren expression from rest
            var parentheticalExpression = exp.Substring(0, indexOfClosingParen).Trim();
            exp = exp.Substring(indexOfClosingParen + 1).TrimLeft();

            // Evaluate
            var result = EvaluateExpression(ref parentheticalExpression, sourceLine, evalMode.Paren, out error);

            if (!error.IsError && !parentheticalExpression.IsNullOrEmpty)
                error = new Error(ErrorCode.Invalid_Expression, Error.Msg_InvalidExpression, sourceLine);
            return result;
        }

        /// <summary>
        /// The string should not include the initial opening paren
        /// </summary>
        /// <param name="exp"></param>
        /// <returns></returns>
        private int FindIndexOfClosingParen(StringSection exp) {
            int parenLevel = 1;
            for (int i = 0; i < exp.Length; i++) {
                char c = exp[i];
                if (c == '(')
                    parenLevel++;
                else if (c == ')')
                    parenLevel--;

                if (parenLevel == 0)
                    return i;
            }
            return -1;
        }

        /// <summary>Gets the index of the unary operator at the beginning of 'exp', or -1 if none found. Operator is removed from exp.</summary>
        private int GetUnaryPostOp(ref StringSection exp) {
            return GetUnaryOp_Internal(ref exp, UnaryExpressionOperator.PostOperators);
        }
        
        /// <summary>Gets the index of the unary operator at the beginning of 'exp', or -1 if none found. Operator is removed from exp.</summary>
        private int GetUnaryPreOp(ref StringSection exp) {
            return GetUnaryOp_Internal(ref exp, UnaryExpressionOperator.PreOperators);
        }

        private static int GetUnaryOp_Internal(ref StringSection exp, UnaryExpressionOperator[] operatorList) {
            for (int iOperator = 0; iOperator < operatorList.Length; iOperator++) {
                string oper = operatorList[iOperator].Symbol;

                bool longEnough = exp.Length >= oper.Length;
                if (longEnough) {
                    bool match = true; // Optimism 

                    for (int iChar = 0; iChar < oper.Length; iChar++) {
                        if (exp[iChar] != oper[iChar])
                            match = false;
                    }

                    if (match) {
                        exp = exp.Substring(oper.Length).TrimLeft();
                        return iOperator;
                    }
                }
            } // for iOperator

            return -1;
        }

        struct EvaluationStackFrame
        {
            public List<int> unary_PreOpIndexList;
            public List<int> unary_PostOpIndexList;
            public List<ParseStackItem> binary_ParseStack;

            public int sourceLine;
            public evalMode ExpressionType;

        }






        /// <summary>
        /// Returns a StringSection containing a numeric string, or a zero-length string if there no numeric string is found.
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        private static StringSection GetHexString(StringSection source) {
            int i = 0;
            while (i < source.Length) {
                char c = char.ToUpper(source[i]);
                if (char.IsDigit(c) || (c >= 'A' & c <= 'F')) {
                    i++;
                } else {
                    // i is first invalid char
                    return source.Substring(0, i);
                }
            }

            // Number ran to end of string
            return source;
        }
        /// <summary>
        /// Returns a StringSection containing a numeric string, or a zero-length string if there no numeric string is found.
        /// </summary>
        private static StringSection GetDecString(StringSection source) {
            int i = 0;
            while (i < source.Length) {
                if (char.IsDigit(source[i])) {
                    i++;
                } else {
                    // i is first invalid char
                    return source.Substring(0, i);
                }
            }

            // Number ran to end of string
            return source;
        }
        /// <summary>
        /// Returns a StringSection containing a numeric string, or a zero-length string if there no numeric string is found.
        /// </summary>
        private static StringSection GetBinString(StringSection source) {
            int i = 0;
            while (i < source.Length) {
                char c = source[i];
                if (c == '0' | c == '1') {
                    i++;
                } else {
                    // i is first invalid char
                    return source.Substring(0, i);
                }
            }

            // Number ran to end of string
            return source;
        }


        private void SetSymbolValue(Identifier name, LiteralValue value, out Error error) {
            ValueNamespace.SetValue(name, value,false, out error);
        }

        private LiteralValue? GetSymbolValue(Identifier name) {
            LiteralValue result;
            if (ValueNamespace.TryGetValue(name, out result)) {
                return result;
            } else {
                if (ErrorOnUndefinedSymbols) {
                    return null;
                } else {
                    return new LiteralValue(0, false);
                }
            }

        }

        /// <summary>
        /// Returns a symbol name (including namespace, e.g. "regs::ppuctrl", or a zero-length string of no symbol was found.
        /// </summary>
        /// <param name="exp"></param>
        /// <returns></returns>
        private Identifier ParseSymbol(ref StringSection exp) {
            if (exp.Length < 1 || !(char.IsLetter(exp[0]) && exp[0] != '_'))
                return Identifier.Empty;

            int i = 1;
            int nameStart = 0;
            string nspace = null;

            while (i < exp.Length) {
                char c = exp[i];
                if (char.IsLetter(c) | char.IsDigit(c) | c == '_') {
                    i++;
                } else {
                    if (nspace == null && c == ':' && i < (exp.Length + 1) && exp[i + 1] == ':') {
                        // we've parsed "name::" so far
                        nspace = exp.Substring(0, i).ToString();
                        i += 2;
                        nameStart = i;
                    } else {
                        var name = exp.Substring(nameStart, i - nameStart).ToString();
                        exp = exp.Substring(i).TrimLeft();
                        return new Identifier(name, nspace);
                    }
                }
            }
            var result = exp.Substring(nameStart, i - nameStart).ToString();
            exp = StringSection.Empty; // Entire string was a symbol
            return new Identifier(result, nspace);
        }

        public bool TryParseSimpleValue(StringSection expression, out LiteralValue value) {
            Error error;
            value = new LiteralValue();

            if (expression[0] == '$') { // Hex
                expression = expression.Substring(1).TrimRight();
                var numberPart = GetHexString(expression);
                if (numberPart.Length != expression.Length) return false;
                value = ParseHex(numberPart, out error);
                if (error.IsError) return false;
            } else if (expression[0] == '%') { // Bin
                expression = expression.Substring(1).TrimRight();
                var numberPart = GetBinString(expression);
                if (numberPart.Length != expression.Length) return false;
                value = ParseBin(numberPart, out error);
                if (error.IsError) return false;
            } else if (char.IsDigit(expression[0])) { // Dec
                var numberPart = GetDecString(expression);
                if (numberPart.Length != expression.Length) return false;
                value = ParseDec(numberPart, out error);
                if (error.IsError) return false;
            } else if (char.IsLetter(expression[0])) { // Variable
                var varName = ParseSymbol(ref expression);
                if (varName.IsEmpty) return false; // This condition should never be true, but check to be safe
                if (expression.Trim().Length != 0) // If there is remaining text, this is not a 'simple value'
                    return false;
                
                bool valueDefined = ValueNamespace.TryGetValue(varName, out value);
                if (!valueDefined) return false;
            } else { // ???
                return false;
            }

            // Profit
            return true;
        }


        /// <summary>
        /// Tries to parse the entire string as a literal value.
        /// </summary>
        /// <param name="expression">A string that may contain a literal value (including preceeding $ or %)</param>
        /// <param name="value">Returns the value.</param>
        /// <returns>True if the string represented a value, otherwise false.</returns>
        public static bool TryParseLiteral(StringSection expression, out LiteralValue value) {
            Error error;
            value = default(LiteralValue);

            char firstChar = expression[0];
            if (firstChar == '$') { // Hex
                expression = expression.Substring(1);
                var numberPart = GetHexString(expression);
                if (numberPart.Length != expression.Length)
                    return false;

                value = ParseHex(numberPart, out error);
                if (error.IsError) return false;
            } else if (firstChar == '%') { // Bin
                expression = expression.Substring(1);
                var numberPart = GetBinString(expression);
                if (numberPart.Length != expression.Length)
                    return false;

                value = ParseBin(numberPart, out error);
                if (error.IsError) return false;
            } else if (char.IsDigit(firstChar)) { // Dec
                var numberPart = GetDecString(expression);
                if (numberPart.Length != expression.Length)
                    return false;

                value = ParseDec(numberPart, out error);
                if (error.IsError) return false;
            } else { // ???
                return false;
            }

            // Profit
            return true;
        }

        private static LiteralValue ParseDec(StringSection valueExpression, out Error error) {
            error = Error.None;

            int value;
            if (!int.TryParse(valueExpression.ToString(), out value))
                error = new Error(ErrorCode.Invalid_Number, Error.Msg_BadNumberFormat);
            else if (value < short.MinValue || value > ushort.MaxValue)
                error = new Error(ErrorCode.Invalid_Number, Error.Msg_ValueOutOfRange);
            
            bool isWord = (value > 255 | value < -128) || (valueExpression[0] == '0' & valueExpression.Length > 1);
            return new LiteralValue((ushort)value, !isWord);
        }

        private static LiteralValue ParseBin(StringSection valueString, out Error error) {
            error = Error.None;

            bool isByte = valueString.Length <= 8;

            if (valueString.Length < 1 || valueString.Length > 16) {
                error = new Error(ErrorCode.Invalid_Number, Error.Msg_BadNumberFormat);
                return default(LiteralValue);
            }

            int result = 0;
            for (int i = 0; i < valueString.Length; i++) {
                result <<= 1;
                char c = valueString[i];
                if (c == '1')
                    result |= 1;
                else if (c != '0') {// Not a '0' or '1', invalid.
                    error = new Error(ErrorCode.Invalid_Number, Error.Msg_BadNumberFormat);
                    return default(LiteralValue);
                }
            }
            return new LiteralValue((ushort)result, isByte);
        }

        /// <summary>
        /// Parses a string as a hexadecimal value, throws an exception if the value is invalid.
        /// </summary>
        /// <param name="valueString">The string containing the hex value, not including the "$".</param>
        /// <returns></returns>
        private static LiteralValue ParseHex(StringSection valueString, out Error error) {
            error = Error.None;

            bool isByte = valueString.Length <= 2;

            int result;
            if (!int.TryParse(valueString.ToString(), System.Globalization.NumberStyles.HexNumber, null, out result)) {
                error = new Error(ErrorCode.Invalid_Number, Error.Msg_BadNumberFormat);
            } else if (result < short.MinValue || result > ushort.MaxValue) {
                error = new Error(ErrorCode.Invalid_Number, Error.Msg_ValueOutOfRange);
            }
                
            return new LiteralValue((ushort)result, isByte);
        }

        private static readonly LiteralValue trueValue = new LiteralValue(0xFF, true);
        private static readonly LiteralValue falseValue = new LiteralValue(0xFF, true);
        private static bool AsBool(LiteralValue value) {
            if (value.IsByte)
                return (value.Value & 0xFF) != 0;
            else
                return value.Value != 0;
        }
        /// <summary>
        /// Creates a LiteralValue object, or returns NULL if the value is out of range (overflow);
        /// </summary>
        /// <param name="value"></param>
        /// <param name="isByte"></param>
        /// <returns></returns>
        private LiteralValue? MakeLiteral(int value, bool isByte) {
            if (OverflowChecking) {
                if (SignedMode) {
                    if (isByte) {
                        if (value < -128 || value > 127)
                            return null;
                    } else {
                        if (value < -32768 || value > 32767)
                            return null;
                    }
                } else {
                    if (value < 0)
                        return null;
                    if (value > (isByte ? 0xFF : 0xFFFF))
                        return null;
                }
            }
            
            if (isByte) {
                return new LiteralValue((byte)value, true);
            } else {
                return new LiteralValue((ushort)value, false);
            }
        }


    }
}
