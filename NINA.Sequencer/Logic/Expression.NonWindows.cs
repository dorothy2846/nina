#if !WINDOWS
using System;
using System.Collections.Generic;
using System.Globalization;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Expressions;

namespace NINA.Sequencer.Logic {
    public class Expression : NINA.Core.Utility.BaseINPC {
        private readonly Dictionary<string, object> parameters = new Dictionary<string, object>();
        private readonly Dictionary<string, UserSymbol> resolved = new Dictionary<string, UserSymbol>();
        private readonly HashSet<string> references = new HashSet<string>();

        public static readonly bool DATE_VALUES_ALLOWED = true;
        public static readonly bool STRING_VALUES_ALLOWED = true;

        public Expression() {
        }

        public Expression(Expression cloneMe, ISequenceEntity context, Action<Expression> validator = null) {
            Definition = cloneMe?.Definition ?? string.Empty;
            Default = cloneMe?.Default ?? double.NaN;
            DefaultString = cloneMe?.DefaultString;
            AutoValue = cloneMe?.AutoValue ?? double.NaN;
            Range = cloneMe?.Range;
            Type = cloneMe?.Type ?? "double";
            Symbol = cloneMe?.Symbol;
            SymbolBroker = cloneMe?.SymbolBroker;
            Context = context;
            Validator = validator;
        }

        public Expression(string definition, ISequenceEntity context) {
            Definition = definition ?? string.Empty;
            Context = context;
        }

        public Expression(string definition, ISequenceEntity context, UserSymbol symbol) : this(definition, context) {
            Symbol = symbol;
            SymbolBroker = symbol?.SymbolBroker;
        }

        public ISequenceEntity Context { get; set; }
        public double Default { get; set; } = double.NaN;
        public double AutoValue { get; set; } = double.NaN;
        public bool IsValid { get; set; }
        public string DefaultString { get; set; }
        public virtual string Definition { get; set; } = string.Empty;
        public bool Dirty { get; set; }
        public virtual string Error { get; set; }
        public string ExprErrors => Error ?? string.Empty;
        public bool ForceAnnotated { get; set; }
        public bool GlobalVolatile { get; set; }
        public bool HasError => !string.IsNullOrEmpty(Error);
        public bool IsAnnotated => IsExpression || ForceAnnotated || Error != null;
        public bool IsExpression { get; set; }
        public bool IsSyntaxError { get; set; }
        public IReadOnlyDictionary<string, object> Parameters => parameters;
        public double[] Range { get; set; }
        public IReadOnlyCollection<string> References => references;
        public IReadOnlyDictionary<string, UserSymbol> Resolved => resolved;
        public string StringValue { get; set; }
        public UserSymbol Symbol { get; set; }
        public ISymbolBroker SymbolBroker { get; set; }
        public string Type { get; set; } = "double";
        public Action<Expression> Validator { get; set; }
        public virtual double Value { get; set; } = double.NaN;
        public string ValueString => StringValue ?? Value.ToString(CultureInfo.InvariantCulture);
        public bool Volatile { get; set; }

        public string RangeString(double? value) {
            return null;
        }

        public static bool JustWarnings(string error) {
            return false;
        }

        public static void ValidateExpressions(IList<string> issues, params Expression[] exprs) {
            foreach (var expr in exprs) {
                if (expr == null) {
                    continue;
                }

                expr.Validate();
                if (!string.IsNullOrEmpty(expr.Error)) {
                    issues?.Add(expr.Error);
                }
            }
        }

        public static void LogOnce(string message) {
            NINA.Core.Utility.Logger.Warning(message);
        }

        public void Evaluate() {
            Evaluate(false);
        }

        public void Evaluate(bool ignoreRoot) {
            Error = null;
            if (double.IsNaN(Value) && !double.IsNaN(Default)) {
                Value = Default;
            }
            Validator?.Invoke(this);
        }

        public void ReferenceRemoved(UserSymbol sym) {
        }

        public void Refresh() {
            Evaluate();
        }

        public void RemoveParameter(string identifier) {
        }

        public void Validate(IList<string> issues) {
            Evaluate(true);
            if (!string.IsNullOrEmpty(Error)) {
                issues?.Add(Error);
            }
        }

        public void Validate() {
            Validate(null);
        }

        public override string ToString() {
            return string.Create(CultureInfo.InvariantCulture, $"Expression: {Definition}");
        }
    }
}
#endif
