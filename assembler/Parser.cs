﻿using System;
using System.Collections.Generic;
using System.Text;
using Romulus;
using System.IO;
using System.Diagnostics;

namespace snarfblasm
{
    class Parser
    {

        private readonly Assembler Assembler;
        private AssemblyData assembly { get; set; }
        public string commentBuffer { get; set; } = "";
        public string[] comments { get; set; } = new String[5000];

        private void StoreComment(int sourceLine) {
            if (commentBuffer != "") {
                comments[sourceLine] = new String(commentBuffer.ToCharArray());
                commentBuffer = "";
            }
        }

        public Parser(Assembler assembler, string source) {
            this.Assembler = assembler;
            this.Assembler.HasPatchSegments = source.IndexOf(".PATCH") != -1 || source.IndexOf(".patch") != -1;
        }

        Identifier mostRecentNamedLabel = new Identifier("_nolabel_", null);
        /// <summary>If true, lines are processed as part of a segment definition.</summary>
        bool DefsegInProgress = false;
        /// <summary>Name of segment being defined. (Applicable when DefsegInProgress is true.)</summary>
        string segName = null;

        List<SegmentAttribute> segAttributes;


        /// <summary>
        /// Parses code from a list of sub-strings. (Useful if the code
        /// is in the form of a single long string.)
        /// </summary>
        /// <param name="lines"></param>
        public void ParseCode(IList<StringSection> lines) {
            assembly = Assembler.Assembly;

            Error error;
            for (int i = 0; i < lines.Count; i++) {
                ParseLine(lines[i], i, out error);

                if (error.Code != ErrorCode.None) {
                    Assembler.AddError(error);
                }
            }

        }




        /// <summary>
        ///
        /// </summary>
        /// <param name="line">The line of code to parse.</param>
        /// <param name="sourceLine">The line number of the code file.</param>
        void ParseLine(StringSection line, int iSourceLine, out Error error) {
            line = line.TrimLeft();
            string lineComment = ParseComments(ref line);

            // string together comments that have no code on the same line, otherwise they wouldn't be output
            if (lineComment != "")
            {
                if (commentBuffer == "") {
                    commentBuffer = lineComment;
                } else {
                    commentBuffer += "\n" + lineComment;
                }
            }

            line = line.TrimRight();

            if (DefsegInProgress) {
                ParseDefsegLine(line, iSourceLine, out error);
            }

            error = Error.None;

            int newInstructionIndex = assembly.ParsedInstructions.Count;

            bool loopLabel;
            do { // This loop allows us to parse alternating named and anonymous labels (* label: +++ -- anotherLabel:)
                loopLabel = false
                    | ParseNamedLabels(ref line, newInstructionIndex, iSourceLine)
                    | ParseAnonymousLabel(ref line, newInstructionIndex, iSourceLine);
            } while (loopLabel);

            if (line.IsNullOrEmpty || line[0] == ';') {
                // Nothing on this line
                return;
            }

            // Dot-prefixed directive
            if (line[0] == '.') {
                var lineCopy = line;

                line = line.Substring(1);
                var directiveName = GrabSimpleName(ref line).ToString();
                line = line.TrimLeft();

                if (directiveName.Equals("DEFSEG")) {
                    var segmentName = GrabSimpleName(ref line);
                    line = line.TrimLeft();

                    if (segmentName.Length == 0) {
                        error = new Error(ErrorCode.Expected_Name, Error.Msg_ExpectedName, iSourceLine);
                    } else if (line.Length > 0) {
                        error = new Error(ErrorCode.Invalid_Directive_Value, string.Format(Error.Msg_InvalidSymbolName_name, lineCopy));
                    } else {
                        DefsegInProgress = true;

                        segName = segmentName.ToString();
                        segAttributes = new List<SegmentAttribute>();

                        return;
                    }
                } else if (!ParseDirective(directiveName, line, iSourceLine, out error)) {
                    error = new Error(ErrorCode.Directive_Not_Defined, string.Format(Error.Msg_DirectiveUndefined_name, directiveName), iSourceLine);
                    return;
                }
                return;
            }

            var symbol = GrabIdentifier(ref line);
            //var symbol = GrabSimpleName(ref line);
            // if (symbol.IsEmpty) break;
            line = line.TrimLeft();

            // store comment for later output
            StoreComment(iSourceLine);

            if (ParseDirective(symbol, line, iSourceLine, out error))
                return;
            else if (symbol.IsSimple && ParseInstruction(symbol.name, line, iSourceLine, out error)) {
                return;
            } else if ((line.Length > 0 && line[0] == '=') || (line.Length > 1 && line[0] == ':' && line[1] == '=')) { // Assignments and label assignments
                // := is a cross between a label and assignment: It declares a label with an explicit value.
                bool isLabel = (line[0] == ':');

                var expression = line.Substring(1).TrimLeft();
                if (isLabel) expression = expression.Substring(1).TrimLeft();

                if (expression.IsNullOrEmpty) {
                    error = new Error(ErrorCode.Expected_Expression, Error.Msg_ExpectedValue, iSourceLine);
                } else {
                    ParseAssignment(symbol, isLabel, expression, iSourceLine);
                }
            } else {
                error = new Error(ErrorCode.Unexpected_Text, Error.Msg_BadLine, iSourceLine);
            }
        }

        private void ParseDefsegLine(StringSection line, int iSourceLine, out Error error) {
            error = Error.None;
            line = line.Trim();
            if (line.IsNullOrEmpty) return;
            if (line.Equals(".ENDDEF", true)) {
                GenerateSegDef(false);
                DefsegInProgress = false;
                return;
            } else if (line.Equals(".SEGMENT")) {
                GenerateSegDef(true);
                DefsegInProgress = false;
                // Todo: generate .SEGMENT directive for the just-defined segment
                return;
            }

            var iEquals = line.IndexOf('=');
            if (iEquals < 0) {
                error = new Error(ErrorCode.Expected_SegAttr, Error.Msg_ExpectedSegAttr, iSourceLine);
                return;
            }

            // Get name and value
            StringSection name, valueString;
            line.Split(iEquals, out name, out valueString);
            name = name.TrimRight();
            valueString = valueString.TrimLeft();
            if (name.IsNullOrEmpty) {
                error = new Error(ErrorCode.Expected_Name, Error.Msg_ExpectedName, iSourceLine);
            } else if (valueString.IsNullOrEmpty) {
                error = new Error(ErrorCode.Expected_Expression, Error.Msg_ExpectedValue, iSourceLine);
            }
            if (error.IsError) return;

            segAttributes.Add(new SegmentAttribute(name.ToString(), valueString.ToString()));

        }

        /// <param name="enterSegment">If true, a SEGMENT directive will be generated after the DEFSEG directive.</param>
        private void GenerateSegDef(bool enterSegment) {

            //assembly.Directives.Add(new OrgDirective(NextInstructionIndex, sourceLine, new AsmValue(line.ToString())));
        }



        private int NextInstructionIndex { get { return assembly.ParsedInstructions.Count; } }
        private void ParseAssignment(Identifier symbolName, bool isLabel, StringSection expression, int iSourceLine) {
            // Call site SHOULD check for this condition and specify an Error.
            if (expression.Length == 0) throw new SyntaxErrorException("Expected: expression.", iSourceLine);

            AsmValue assignedValue;

            LiteralValue assignedLiteral;
            if (ExpressionEvaluator.TryParseLiteral(expression, out assignedLiteral)) {
                assignedValue = new AsmValue(assignedLiteral);
            } else {
                assignedValue = new AsmValue(expression.ToString());
            }

            assembly.Directives.Add(new Assignment(NextInstructionIndex, iSourceLine, symbolName, isLabel, assignedValue));

        }

        private bool ParseInstruction(StringSection instruction, StringSection operand, int iSourceLine, out Error error) {
            error = Error.None;
            if (operand.Length > 0 && operand[0] == '@') { }
            var addressing = ParseAddressing(ref operand);
            int opcodeVal = Opcode.FindOpcode(instruction, addressing, Assembler.AllowInvalidOpcodes);

            // Some instructions only support zero-page addressing variants of certain addressing modes
            if ((OpcodeError)opcodeVal == OpcodeError.InvalidAddressing) {
                var newAddressing = addressing;
                if (TryZeroPageEqivalent(ref newAddressing)) {
                    opcodeVal = Opcode.FindOpcode(instruction, newAddressing, Assembler.AllowInvalidOpcodes);
                    if (opcodeVal >= 0) addressing = newAddressing; // Keep the zero-page addressing
                }
            }

            if (opcodeVal < 0) {
                // We don't throw an error if 'instruction' isn't an instruction (the line could be anything else other than instruction)
                if ((OpcodeError)opcodeVal == OpcodeError.UnknownInstruction) {
                    return false;
                } else {
                    SetOpcodeError(iSourceLine, instruction.ToString(), addressing, opcodeVal, out error);
                    return true;
                }
            }

            if (addressing != Opcode.addressing.implied) {
                LiteralValue operandValue = new LiteralValue();

                // Todo: consider method(s) such as assembly.AddInstruction
                if (ExpressionEvaluator.TryParseLiteral(operand, out operandValue)) {
                    assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, operandValue, iSourceLine));
                } else if (operand.Length > 0) {
                    assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, operand.Trim().ToString(), iSourceLine));
                } else { // no operand
                    assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, default(LiteralValue), iSourceLine));
                }
            } else {
                assembly.ParsedInstructions.Add(new ParsedInstruction((byte)opcodeVal, default(LiteralValue), iSourceLine));
            }

            return true;
        }

        private bool TryZeroPageEqivalent(ref Opcode.addressing addressing) {
            switch (addressing) {
                case Opcode.addressing.absolute:
                    addressing = Opcode.addressing.zeropage;
                    return true;
                case Opcode.addressing.absoluteIndexedX:
                    addressing = Opcode.addressing.zeropageIndexedX;
                    return true;
                case Opcode.addressing.absoluteIndexedY:
                    addressing = Opcode.addressing.zeropageIndexedY;
                    return true;
                default:
                    return false;
            }
        }

        private string ParseComments(ref StringSection line) {
            // ; denotes a comment, except within a string
            bool inString = false;
            string comment = "";

            for (int i = 0; i < line.Length; i++) {
                if (inString) {
                    if (line[i] == '\"') { // End of string
                        // unless it is preceeded by a backslash (then it's as escaped quote)
                        if (i == 0 || line[i - 1] != '\\')
                            inString = false;
                    }
                } else {
                    if (line[i] == ';') { // Comment
                        comment = line.Substring(i + 1).Trim().ToString();
                        line = line.Substring(0, i);
                        return comment;
                    } else if (line[i] == '\"') { // Start of string
                        inString = true;
                    }
                }
            }
            return comment;
        }


        static List<char> charBuilder = new List<char>();
        static object ParseLock = new object();

        static char[] escapeCodes = new char[] { 't', 'r', 'n', '\"' };
        static char[] escapeValues = new char[] { '\t', '\r', '\n', '\"' };
        /// <summary>
        /// Returns a char array containing all the characters from a string. Escapes are processed. The specified string
        /// should not include the opening quote. The parsed string will be removed from the string passed in. The closing
        /// quote will not be removed, so that the caller can examine it and verify that there was a closing quote.
        /// </summary>
        /// <param name="section"></param>
        /// <returns></returns>
        public static char[] ParseString(ref StringSection str, out ErrorCode error) {
            error = ErrorCode.None;

            // Only one thread can run this method at a time
            lock (ParseLock) {
                charBuilder.Clear();

                while (str.Length > 0) {
                    char c = str[0];
                    if (c == '\"') {
                        return charBuilder.ToArray();
                    } else if (c == '\\') {
                        str = str.Substring(1);
                        if (str.Length > 0) {
                            c = str[0];

                            int escapeIndex = Array.IndexOf(escapeCodes, c);
                            if (escapeIndex < 0) {
                                error = ErrorCode.Invalid_Escape;
                                return null;
                            } else {
                                charBuilder.Add(escapeValues[escapeIndex]);
                            }
                        } else {
                            error = ErrorCode.Invalid_Escape;
                            return null;
                        }
                    } else {
                        charBuilder.Add(c);
                    }
                    str = str.Substring(1);
                }

                return charBuilder.ToArray();
            }
        }
        /// <summary>
        /// Returns a symbol name (identifier or '$' character), or an empty value if nothing is found.
        /// Will not parse local labels.
        /// </summary>
        /// <param name="exp"></param>
        /// <returns></returns>
        /// <remarks>This function will parse "namespace::identifier" as a single symbol. </remarks>
        private Identifier GrabIdentifier(ref StringSection exp) {
            var line = exp;

            // Check for special '$' variable
            if (exp.Length > 0 && exp[0] == '$' && !char.IsLetter(exp[0]) && !char.IsDigit(exp[0])) {
                // Todo: how can the char be a letter or digit if its '$'?
                return Identifier.CurrentInstruction; //"$";
            }

            if (exp.Length == 0) return Identifier.Empty;
            if (!char.IsLetter(exp[0]) && exp[0] != '@' && exp[0] != '_') return Identifier.Empty;
            var iNamespace = -1;

            int i = 1;
            while (i < exp.Length) {
                char c = exp[i];
                if (char.IsLetterOrDigit(c) | c == '_') {
                    i++;
                } else if (c == ':' && (i + 2 < exp.Length) && exp[i + 1] == ':') {
                    // :: <- namespace char
                    iNamespace = i;
                    i += 2;
                } else {
                    break; // end of identifier
                }
            }

            exp = exp.Substring(i); // Chop off whatever we found

            if (iNamespace < 0) {
                return new Identifier(line.Substring(0, i).ToString(), null);
            } else {
                return new Identifier(line.Substring(0, iNamespace).ToString(), line.Substring(iNamespace + 2, i - iNamespace - 2).ToString());
            }
        }

        private static bool IsIdentifierChar(char c) {
            return c == '_' | char.IsLetterOrDigit(c);
        }

        private StringSection GrabSimpleName(ref StringSection exp) {
            // Check for special '$' variable
            if (exp.Length > 0 && exp[0] == '$') {
                exp = exp.Substring(1);
                return "$";
            }

            if (exp.Length == 0) return StringSection.Empty;
            if (!char.IsLetter(exp[0]) && exp[0] != '@' && exp[0] != '_') return StringSection.Empty;

            int i = 1;
            while (i < exp.Length) {
                char c = exp[i];
                if (char.IsLetter(c) | char.IsDigit(c) | c == '_') {
                    i++;
                } else {
                    // Return up to i
                    var result = exp.Substring(0, i);
                    exp = exp.Substring(i);
                    return result;
                }
            }

            // Return whole thing
            var temp = exp;
            exp = StringSection.Empty;
            return temp;
        }

        private bool ParseDirective(Identifier directiveName, StringSection line, int sourceLine, out Error error) {
            if (!directiveName.IsSimple) {
                error = new Error(ErrorCode.Directive_Not_Defined, string.Format(Error.Msg_DirectiveUndefined_name, directiveName.ToString()), sourceLine);
                return false;
            } else {
                return ParseDirective(directiveName.name, line, sourceLine, out error);
            }
        }
        /// <summary>
        /// Returns true of a directive was parsed, even if it was not parsed successfully due to an error
        /// </summary>
        /// <param name="directiveName"></param>
        /// <param name="line"></param>
        /// <param name="sourceLine"></param>
        /// <param name="error"></param>
        /// <returns></returns>
        private bool ParseDirective(string directiveName, StringSection line, int sourceLine, out Error error) {
            error = Error.None;

            if (StringEquals(directiveName, "org", true)) {
                assembly.Directives.Add(new OrgDirective(NextInstructionIndex, sourceLine, new AsmValue(line.ToString())));
            } else if (StringEquals(directiveName, "base", true)) {
                assembly.Directives.Add(new BaseDirective(NextInstructionIndex, sourceLine, new AsmValue(line.ToString())));
            } else if (StringEquals(directiveName, "incbin", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new IncBinDirective(NextInstructionIndex, sourceLine, line, Assembler.asmPath));
            } else if (StringEquals(directiveName, "error", true)) {
                assembly.Directives.Add(new ErrorDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "patch", true)) {
                assembly.Directives.Add(new PatchDirective(NextInstructionIndex, sourceLine, line.ToString()));
            } else if (StringEquals(directiveName, "define", true)) {
                var remainder = line.Trim();
                if (GrabSimpleName(ref remainder).Length == line.Length) { // line should contain a only a symbol
                    assembly.Directives.Add(new DefineDirective(NextInstructionIndex, sourceLine, line));
                } else {
                    error = new Error(ErrorCode.Expected_LValue, string.Format(Error.Msg_InvalidSymbolName_name, line.ToString()), sourceLine);
                }
            } else if (StringEquals(directiveName, "hex", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new HexDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "db", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Bytes));
            } else if (StringEquals(directiveName, "byte", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Bytes));
            } else if (StringEquals(directiveName, "dw", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Words));
            } else if (StringEquals(directiveName, "word", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Words));
            } else if (StringEquals(directiveName, "data", true)) {
                StoreComment(sourceLine);
                assembly.Directives.Add(new DataDirective(NextInstructionIndex, sourceLine, line, DataDirective.DataType.Implicit));
            } else if (StringEquals(directiveName, "dsb", true)) {
                assembly.Directives.Add(new StorageDirective(NextInstructionIndex, sourceLine, line, StorageDirective.DataType.Bytes));
            } else if (StringEquals(directiveName, "dsw", true)) {
                assembly.Directives.Add(new StorageDirective(NextInstructionIndex, sourceLine, line, StorageDirective.DataType.Words));
            } else if (StringEquals(directiveName, "namespace", true)) {
                assembly.Directives.Add(new NamespaceDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "overflow", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "if", true)
                || StringEquals(directiveName, "else", true)
                || StringEquals(directiveName, "ifdef", true)
                || StringEquals(directiveName, "ifndef", true)
                || StringEquals(directiveName, "endif", true)) {

                assembly.Directives.Add(new ConditionalDirective(NextInstructionIndex, sourceLine, directiveName, line.Trim(), out error));
            } else if (StringEquals(directiveName, "ENUM", true)) {
                assembly.Directives.Add(new EnumDirective(NextInstructionIndex, sourceLine, line));
            } else if (StringEquals(directiveName, "ENDE", true) || StringEquals(directiveName, "ENDENUM", true)) {
                if (line.IsNullOrEmpty) {
                    assembly.Directives.Add(new EndEnumDirective(NextInstructionIndex, sourceLine));
                } else {
                    error = new Error(ErrorCode.Unexpected_Text, Error.Msg_NoTextExpected, sourceLine);
                }
            } else if (StringEquals(directiveName, "signed", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "needdot", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "needcolon", true)) {
                assembly.Directives.Add(new OptionDirective(NextInstructionIndex, sourceLine, directiveName.ToString(), line.Trim().ToString()));
            } else if (StringEquals(directiveName, "alias", true)) {
                var varName = GrabLabel(ref line, false);
                line = line.Trim();

                if (varName.IsEmpty) {
                    error = new Error(ErrorCode.Syntax_Error, Error.Msg_ExpectedText, sourceLine);
                    return true;
                }
                assembly.Directives.Add(new Assignment(NextInstructionIndex, sourceLine, varName, true, new AsmValue(line.ToString())));
            } else {
                return false;
            }

            return true;

        }



        // This list should be sorted alphabetically.
        StringSection[] directiveNames = { "BASE", "ORG", };
        private bool IsDirective(StringSection directiveName) {
            int listStart = 0;
            int listEnd = directiveNames.Length / 2; // Exclusive

            int listCount = listEnd - listStart;
            while (listCount > 1) {
                // first item of second half
                int split = listStart + listCount / 2;

                var splitText = directiveNames[split];
                var compare = StringSection.Compare(directiveName, splitText, true);
                if (compare == 0) return true;
                if (compare > 0) { // directiveName > splitText
                    listStart = split;
                } else {
                    listEnd = split;
                }

                listCount = listEnd - listStart;
            }
            return StringSection.Compare(directiveName, directiveNames[listStart], true) == 0;
        }

        private StringSection ParseDirectiveName(StringSection text) {
            int i = 0;
            while (i < text.Length && Char.IsLetter(text[i])) {
                i++;
            }
            return text.Substring(0, i);
        }

        private static void SetOpcodeError(int sourceLine, string instructionString, Opcode.addressing addressing, int opcodeVal, out Error error) {
            var errorCode = (OpcodeError)opcodeVal;
            switch (errorCode) {
                case OpcodeError.UnknownInstruction:
                    error = new Error(ErrorCode.Invalid_Instruction, Error.Msg_InstructionUndefined, sourceLine);
                    break;
                case OpcodeError.InvalidAddressing:
                    error = new Error(ErrorCode.Invalid_Instruction, Error.Msg_InstructionBadAddressing, sourceLine);
                    break;
                case OpcodeError.InvalidOpcode:
                    error = new Error(ErrorCode.Invalid_Instruction, Error.Msg_OpcodeInvalid, sourceLine);
                    break;
                default:
                    System.Diagnostics.Debug.Fail("Unexpected error.");
                    error = new Error(ErrorCode.Engine_Error, Error.Msg_Engine_InvalidState);
                    break;
            }
        }


        /// <summary>
        /// Parses one anonymous label (both * and +/- types), if found, and removes it from the expression.
        /// </summary>
        /// <param name="line">The text to parse a label from. This text will be modified to remove the label.</param>
        /// <param name="lineNumber">The index of the first instruction that follows the label.</param>
        /// <param name="iSourceLine">The index of the source line the label occurs on.</param>
        /// <returns>True if a label was parsed.</returns>
        private bool ParseAnonymousLabel(ref StringSection line, int iInstruction, int iSourceLine) {
            // Todo: support named +/-/* labels. This means that the anon label collection will need to be able to store names.

            if (line.Length > 0) {
                if (line[0] == '*') {
                    assembly.AnonymousLabels.AddStarLabel(iSourceLine);
                    assembly.TagAnonLabel(iInstruction, iSourceLine);

                    line = line.Substring(1).TrimLeft();
                    // Remove colon if present
                    if (line.Length > 0 && line[0] == ':') line = line.Substring(1).TrimLeft();
                    return true;
                } else if (line[0] == '+' | line[0] == '-') {
                    ParsePlusOrMinusLabel(ref line, line[0], iInstruction, iSourceLine);
                    return true;
                } else if (line[0] == '{') {
                    assembly.AnonymousLabels.AddLeftBraceLabel(iSourceLine);
                    assembly.TagAnonLabel(iInstruction, iSourceLine);
                    line = line.Substring(1).TrimLeft();
                } else if (line[0] == '}') {
                    assembly.AnonymousLabels.AddRightBraceLabel(iSourceLine);
                    assembly.TagAnonLabel(iInstruction, iSourceLine);
                    line = line.Substring(1).TrimLeft();
                }
            }
            return false;
        }



        private void ParsePlusOrMinusLabel(ref StringSection line, char labelChar, int iInstruction, int iSourceLine) {
            int charCount = 1; // Number of times the label char (+ or -) appears
            while (charCount < line.Length && line[charCount] == labelChar)
                charCount++;

            if (labelChar == '+') {
                assembly.AnonymousLabels.AddPlusLabel(charCount, iSourceLine);
            } else if (labelChar == '-') {
                assembly.AnonymousLabels.AddMinusLabel(charCount, iSourceLine);
            } else {
                throw new ArgumentException("Invalid label character for +/- label.", "labelChar");
            }
            assembly.TagAnonLabel(iInstruction, iSourceLine);

            line = line.Substring(charCount).Trim();
            // Remove colon if present
            if (line.Length > 0 && line[0] == ':') line = line.Substring(1).TrimLeft();

        }

        private bool ParseNamedLabels(ref StringSection line, int iParsedLine, int iSourceLine) {
            var lineCopy = line; // Don't want to mutate line if we don't actually do anything
            var labelName = GrabLabel(ref lineCopy, true);
            if (labelName.IsEmpty) return false;

            // Check for nonzero length and that label starts with letter or @ or _
            var simpleName = labelName.name;
            if (simpleName.Length == 0) return false;
            if (!char.IsLetter(simpleName[0]) && simpleName[0] != '@' && simpleName[0] != '_')
                return false;
            // Namespaced labels can't be local
            if (simpleName[0] == '@' && !string.IsNullOrEmpty(labelName.nspace)) return false; // Todo: produce a useful error instead of ignoring invalid syntax

            for (int i = 1; i < simpleName.Length; i++) { // i = 1 because we've already checked zero
                if (!char.IsLetter(simpleName[i]) && !char.IsDigit(simpleName[i]) && simpleName[i] != '_')
                    return false;
            }

            if (simpleName[0] == '@') { // Local label
                simpleName = simpleName.Substring(1); // Remove @
                string fullName = mostRecentNamedLabel + "." + simpleName.ToString(); // example: SomeFunction.LoopTop
                assembly.Labels.Add(new NamespacedLabel(fullName, null, iParsedLine, iSourceLine, true));
            } else { // Normal label
                assembly.Labels.Add(new NamespacedLabel(labelName.name, labelName.nspace, iParsedLine, iSourceLine, false));
            }
            line = lineCopy; //line.Substring(iColon + 1).TrimLeft();

            return true;
        }

        /// <summary>Gets the label that begins at position 0 within the specified string, and updates the string to remove the parsed label name.</summary>
        /// <param name="line">String to parse. Will be modified to remove the parsed label name.</param>
        /// <param name="checkForAssign">If true, the presence of a := symbol following a name will cause the name to NOT be parsed</param>
        /// <returns>A string containing a label, or an empty string.</returns>
        /// <remarks>Whitespace preceeding the label name will be cropped out.</remarks>
        private Identifier GrabLabel(ref StringSection line, bool checkForAssign) {
            var iColon = line.IndexOf(':');

            if (iColon == -1) return Identifier.Empty;
            if (checkForAssign && (line.Length - 1 > iColon) && (line[iColon + 1] == '=')) {
                return Identifier.Empty; // "x := y" is not a label
            }

            // Namespace::label
            if (line.Length > iColon + 1) {
                if (line[iColon + 1] == ':') { // '::' is a namespace operator
                    var nspace = line.Substring(iColon).Trim();
                    var restOfLine = line.Substring(iColon + 2);
                    var iColon2 = restOfLine.IndexOf(':');
                    if (iColon2 >= 0) {
                        if (checkForAssign && restOfLine.Length - 1 > iColon2 && restOfLine[iColon2 + 1] == '=') {
                            return Identifier.Empty; // "n::x := y" is not a label
                        }

                        var label = restOfLine.Substring(iColon2).Trim();
                        line = restOfLine.Substring(iColon2 + 1);
                        return new Identifier(label.ToString(), nspace.ToString());
                    } else {
                        return Identifier.Empty;
                    }
                }
            }

            var result = line.Substring(0, iColon).Trim();
            line = line.Substring(iColon + 1);
            return new Identifier(result.ToString(), null);
        }

        private static bool StringEquals(StringSection a, StringSection b, bool ignoreCase) {
            return StringSection.Compare(a, b, ignoreCase) == 0;
        }

        ///// <summary>
        ///// Returns a value between 0 and 255, or -1 if no opcode was found, or -2 if the addressing mode is not available for the instruction..
        ///// </summary>
        ///// <param name="instruction"></param>
        ///// <param name="addressing"></param>
        ///// <returns></returns>
        //private static int FindOpcode(StringSection instruction, Opcode.addressing addressing, bool allowInvalidOpcodes) {
        //    var ops = Opcode.allOps;
        //    bool instructionFound = false;
        //    bool foundInstructionInvalid = false;

        //    for (int i = 0; i < Opcode.allOps.Length; i++) {
        //        if (StringEquals(ops[i].name, instruction, true)) {
        //            // Note that the instruction exists. We need to tell the user whether
        //            // an instruction does not exist or the desired addressing mode is not available.
        //            instructionFound = true;

        //            var addrMode = ops[i].addressing;

        //            // Branch instructions will be treated as absolute until they are actually encoded.
        //            if (addrMode == Opcode.addressing.relative) addrMode = Opcode.addressing.absolute;

        //            if (addressing == addrMode) {
        //                if (ops[i].valid | allowInvalidOpcodes)
        //                    return i;
        //                else
        //                    foundInstructionInvalid = true;
        //            }
        //        }
        //    }

        //    if (instructionFound) {
        //        if (foundInstructionInvalid) {
        //            return (int)OpcodeError.InvalidOpcode;
        //        } else {
        //            return (int)OpcodeError.InvalidAddressing;
        //        }
        //    } else {
        //        return (int)OpcodeError.UnknownInstruction;
        //    }
        //}


        bool ParseOperand(StringSection operand, out LiteralValue value, out string expression) {

            if (ExpressionEvaluator.TryParseLiteral(operand, out value)) {
                expression = null;
                return true;
            } else if (operand.Length > 0) {
                value = default(LiteralValue);
                expression = operand.ToString();
                return true;
            } else {
                value = default(LiteralValue);
                expression = null;
                return false;
            }
        }

        /// <summary>
        /// Returns the addressing mode for the operand. Zero-page addressing modes are not considered (this can be addressed when omitting opcodes).
        /// 'Accumulator' addressing is considered implied. The 'operand'
        /// paremeter is updated to remove any addressing characters.
        /// </summary>
        /// <param name="operand">The operand string. Must be trimmed.</param>
        /// <param name="addressing"></param>
        /// <returns></returns>
        private Opcode.addressing ParseAddressing(ref StringSection operand) {
            int opLen = operand.Length;

            // Accumulator or implied
            if (opLen == 0 || (opLen == 1 && Char.ToUpper(operand[0]) == 'A')) {
                return Opcode.addressing.implied;

            }

            // Immediate
            if (operand[0] == '#') {
                operand = operand.Substring(1).Trim();
                return Opcode.addressing.immediate;
            }

            // ,X
            if (char.ToUpper(operand[opLen - 1]) == 'X') {
                var sansX = operand.Substring(0, opLen - 1).TrimRight();
                if (sansX.Length > 0 && sansX[sansX.Length - 1] == ',') {
                    sansX = sansX.Substring(0, sansX.Length - 1).Trim();

                    // Update operand to remove addressing
                    operand = sansX;
                    return Opcode.addressing.absoluteIndexedX;
                }

            }
            // (),Y
            // ,Y
            if (char.ToUpper(operand[opLen - 1]) == 'Y') {
                var sansY = operand.Substring(0, opLen - 1).TrimRight();
                if (sansY.Length > 0 && sansY[sansY.Length - 1] == ',') {
                    sansY = sansY.Substring(0, sansY.Length - 1).Trim();

                    //(),Y
                    if (sansY.Length > 0 && sansY[0] == '(' && sansY[sansY.Length - 1] == ')') {
                        sansY = sansY.Substring(1, sansY.Length - 2).Trim();

                        operand = sansY;
                        return Opcode.addressing.indirectY;
                    }

                    operand = sansY;
                    return Opcode.addressing.absoluteIndexedY;
                }

            }

            // ()
            // (,X)
            if (operand[0] == '(' && operand[opLen - 1] == ')') {
                operand = operand.Substring(1, opLen - 2).Trim();
                opLen = operand.Length;

                // (,X)
                if (opLen > 0 && char.ToUpper(operand[opLen - 1]) == 'X') {
                    var sansX = operand.Substring(0, opLen - 1).TrimRight();
                    if (sansX.Length > 0 && sansX[sansX.Length - 1] == ',') {
                        sansX = sansX.Substring(0, sansX.Length - 1).Trim();

                        // Update operand to remove addressing
                        operand = sansX;
                        return Opcode.addressing.indirectX;
                    }

                }

                return Opcode.addressing.indirect;
            }


            ////}

            // If there are no addressing chars, it must be absolute (absolute vs. zero page is determined later)
            return Opcode.addressing.absolute;

        }

        bool EndsWith(StringSection text, string ending) {
            int diff = text.Length - ending.Length;
            if (diff < 0) return false;
            for (int i = 0; i < ending.Length; i++) {
                if (text[i + diff] != ending[i])
                    return false;
            }
            return true;
        }


    }




    /// <summary>
    /// Defines the interface used to get/set variable and label values.
    /// </summary>
    interface IValueNamespace
    {
        string CurrentNamespace { get; set; }

        int GetForwardLabel(int labelLevel, int iSourceLine);
        int GetBackwardLabel(int labelLevel, int iSourceLine);
        int GetForwardBrace(int labelLevel, int iSourceLine);
        int GetBackwardBrace(int labelLevel, int iSourceLine);

        void SetValue(Identifier name, LiteralValue value, bool isFixed, out Error error);
        //void SetValue(StringSection name, StringSection nspace, LiteralValue value, bool isFixed, out Error error);
        LiteralValue GetValue(Identifier name);
        //LiteralValue GetValue(StringSection name, StringSection nspace);
        //bool TryGetValue(StringSection name, out LiteralValue result);
        //bool TryGetValue(StringSection name, StringSection nspace, out LiteralValue result);
        bool TryGetValue(Identifier name, out LiteralValue result);
    }

}
