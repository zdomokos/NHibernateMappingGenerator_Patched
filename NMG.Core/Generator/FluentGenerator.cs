﻿using System;
using System.CodeDom;
using System.Globalization;
using System.Linq;
using System.Text;
using NMG.Core.Domain;
using NMG.Core.Fluent;
using NMG.Core.TextFormatter;

namespace NMG.Core.Generator
{
    public class FluentGenerator : AbstractCodeGenerator
    {
        private readonly ApplicationPreferences appPrefs;

        public FluentGenerator(ApplicationPreferences appPrefs, Table table) : base(appPrefs.FolderPath, "Mapping", appPrefs.TableName, appPrefs.NameSpaceMap, appPrefs.AssemblyName, appPrefs.Sequence, table, appPrefs)
        {
            this.appPrefs = appPrefs;
            language = this.appPrefs.Language;
        }

        public override void Generate(bool writeToFile = true)
        {
            var pascalCaseTextFormatter = new PascalCaseTextFormatter { PrefixRemovalList = appPrefs.FieldPrefixRemovalList };
            var className = $"{appPrefs.ClassNamePrefix}{pascalCaseTextFormatter.FormatSingular(Table.Name)}{"Map"}";
            var compileUnit = GetCompleteCompileUnit(className);
            var generateCode = GenerateCode(compileUnit, className);

            if (writeToFile)
            {
                WriteToFile(generateCode, className);
            }
            else
            {
                GeneratedCode = WriteToString(compileUnit, GetCodeDomProvider());
            }
        }

        protected override string CleanupGeneratedFile(string generatedContent)
        {
            return generatedContent;
        }

        public CodeCompileUnit GetCompleteCompileUnit(string className)
        {
            var codeGenerationHelper = new CodeGenerationHelper();
            var compileUnit = codeGenerationHelper.GetCodeCompileUnit(nameSpace, className);

            var newType = compileUnit.Namespaces[0].Types[0];
            
            newType.IsPartial = appPrefs.GeneratePartialClasses;
            var pascalCaseTextFormatter = new PascalCaseTextFormatter { PrefixRemovalList = appPrefs.FieldPrefixRemovalList };
            newType.BaseTypes.Add(
	            $"ClassMap<{appPrefs.ClassNamePrefix}{pascalCaseTextFormatter.FormatSingular(Table.Name)}>");

            var constructor = new CodeConstructor {Attributes = MemberAttributes.Public};
            constructor.Statements.Add(new CodeSnippetStatement(TABS + "Table(\"" + Table.Name + "\");"));
            if (appPrefs.UseLazy)
                constructor.Statements.Add(new CodeSnippetStatement(TABS + "LazyLoad();"));

            if(UsesSequence)
            {
                var fieldName = FixPropertyWithSameClassName(Table.PrimaryKey.Columns[0].Name, Table.Name);
				constructor.Statements.Add(new CodeSnippetStatement(
					TABS +
					$"Id(x => x.{Formatter.FormatText(fieldName)}).Column(x => x.{fieldName}).GeneratedBy.Sequence(\"{appPrefs.Sequence}\")"));
            }
            else if (Table.PrimaryKey !=null && Table.PrimaryKey.Type == PrimaryKeyType.PrimaryKey)
            {
                var fieldName = FixPropertyWithSameClassName(Table.PrimaryKey.Columns[0].Name, Table.Name);
                constructor.Statements.Add(GetIdMapCodeSnippetStatement(this.appPrefs, Table, Table.PrimaryKey.Columns[0].Name, fieldName, Table.PrimaryKey.Columns[0].DataType, Formatter));
            }
            else if (Table.PrimaryKey != null)
            {
                constructor.Statements.Add(GetIdMapCodeSnippetStatement(Table.PrimaryKey, Table, Formatter));
            }

            // Many To One Mapping
            foreach (var fk in Table.ForeignKeys.Where(fk => fk.Columns.First().IsForeignKey && appPrefs.IncludeForeignKeys))
            {
                var propertyName = appPrefs.NameFkAsForeignTable ? fk.UniquePropertyName : fk.Columns.First().Name;
                string name = propertyName;
                propertyName = Formatter.FormatSingular(propertyName);
                var fieldName = FixPropertyWithSameClassName(propertyName, Table.Name);
                var pkAlsoFkQty = (from fks in Table.ForeignKeys.Where(fks => fks.UniquePropertyName == name) select fks).Count();
                if (pkAlsoFkQty > 1)
                {
                    constructor.Statements.Add(new CodeSnippetStatement(
	                    TABS +
	                    $"References(x => x.{fieldName}).Column(\"{fk.Columns.First().Name}\").ForeignKey(\"{fk.Columns.First().ConstraintName}\");"));
                }
                else
                {
                    constructor.Statements.Add(new CodeSnippetStatement(
	                    TABS + $"References(x => x.{fieldName}).Column(\"{fk.Columns.First().Name}\");"));
                }
                
            }

            // Property Map
            foreach (var column in Table.Columns.Where(x => !x.IsPrimaryKey && (!x.IsForeignKey || !appPrefs.IncludeForeignKeys)))
            {
                var propertyName = Formatter.FormatText(column.Name);
                var fieldName = FixPropertyWithSameClassName(propertyName, Table.Name);
                var columnMapping = new DBColumnMapper().Map(column, fieldName, Formatter, appPrefs.IncludeLengthAndScale);
                constructor.Statements.Add(new CodeSnippetStatement(TABS + columnMapping));
            }

            // Bag (HasMany in FluentMapping)
            if (appPrefs.IncludeHasMany)
                Table.HasManyRelationships.ToList().ForEach(x => constructor.Statements.Add(new OneToMany(Formatter).Create(x)));

            newType.Members.Add(constructor);
            return compileUnit;
        }

        private static string FixPropertyWithSameClassName(string property, string className)
        {
            return property.ToLowerInvariant() == className.ToLowerInvariant() ? property + "Val" : property;
        }

        protected override string AddStandardHeader(string entireContent)
        {
            entireContent = "using " + appPrefs.NameSpace + "; " + entireContent;
            entireContent = "using FluentNHibernate.Mapping;" + Environment.NewLine + entireContent;
            return base.AddStandardHeader(entireContent);
        }

        private static CodeSnippetStatement GetIdMapCodeSnippetStatement(ApplicationPreferences appPrefs, Table table, string pkColumnName, string propertyName, string pkColumnType, ITextFormatter formatter)
        {
            var dataTypeMapper = new DataTypeMapper();
            bool isPkTypeIntegral = (dataTypeMapper.MapFromDBType(appPrefs.ServerType, pkColumnType, null, null, null)).IsTypeIntegral();

            string idGeneratorType = (isPkTypeIntegral ? "GeneratedBy.Identity()" : "GeneratedBy.Assigned()");
            var fieldName = FixPropertyWithSameClassName(propertyName, table.Name);
            var pkAlsoFkQty = (from fk in table.ForeignKeys.Where(fk => fk.UniquePropertyName == pkColumnName) select fk).Count();
            if (pkAlsoFkQty > 0) fieldName = fieldName + "Id";
             return new CodeSnippetStatement(
	             TABS + $"Id(x => x.{formatter.FormatText(fieldName)}).{idGeneratorType}.Column(\"{pkColumnName}\");");
        }

        private static CodeSnippetStatement GetIdMapCodeSnippetStatement(PrimaryKey primaryKey, Table table, ITextFormatter formatter)
        {
            var keyPropertyBuilder = new StringBuilder(primaryKey.Columns.Count);
            bool first = true;
            foreach (Column pkColumn in primaryKey.Columns)
            {
                var propertyName = formatter.FormatText(pkColumn.Name);
                var fieldName = FixPropertyWithSameClassName(propertyName, table.Name);
                var pkAlsoFkQty = (from fk in table.ForeignKeys.Where(fk => fk.UniquePropertyName == pkColumn.Name) select fk).Count();
                if (pkAlsoFkQty > 0) fieldName = fieldName + "Id";
             var tmp = $".KeyProperty(x => x.{fieldName}, \"{pkColumn.Name}\")";
                keyPropertyBuilder.Append(first ? tmp : "\n" + TABS + "             " + tmp);
                first = false;
            }

            return new CodeSnippetStatement(TABS + $"CompositeId(){keyPropertyBuilder};");
        }
    }

    public static class DataTypeExtensions
    {
        public static bool IsTypeIntegral(this Type typeToCheck)
        {
            return
                typeToCheck == typeof (int) ||
                typeToCheck == typeof (long) ||
                typeToCheck == typeof (uint) ||
                typeToCheck == typeof (ulong);
        }
    }

    public class OneToMany
    {
        public OneToMany(ITextFormatter formatter)
        {
            Formatter = formatter;
        }

        private ITextFormatter Formatter { get; set; }

        public CodeSnippetStatement Create(HasMany hasMany)
        {
        	var hasManySnippet = $"HasMany(x => x.{Formatter.FormatPlural(hasMany.Reference)})";
        	var keySnippet = hasMany.AllReferenceColumns.Count == 1 ? $".KeyColumn(\"{hasMany.ReferenceColumn}\")"
		        : $".KeyColumns({hasMany.AllReferenceColumns.Aggregate("new string[] { ", (a, b) => a + "\"" + b + "\", ", c => c.Substring(0, c.Length - 2) + " }")})";

			return new CodeSnippetStatement(string.Format(AbstractGenerator.TABS + "{0}{1};", hasManySnippet, keySnippet));
		}
	}

    public static class StringExtensions
    {
        public static string Capitalize(this string inputString)
        {
            var textInfo = new CultureInfo("en-US", false).TextInfo;
            return textInfo.ToTitleCase(inputString);
        }
    }
}