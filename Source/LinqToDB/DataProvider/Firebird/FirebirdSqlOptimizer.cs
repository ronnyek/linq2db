﻿using System.Linq;

namespace LinqToDB.DataProvider.Firebird
{
	using Extensions;
	using SqlProvider;
	using SqlQuery;

	class FirebirdSqlOptimizer : BasicSqlOptimizer
	{
		public FirebirdSqlOptimizer(SqlProviderFlags sqlProviderFlags) : base(sqlProviderFlags)
		{
		}

		static void SetNonQueryParameter(IQueryElement element)
		{
			if (element.ElementType == QueryElementType.SqlParameter)
			{
				var p = (SqlParameter) element;
				if (p.SystemType == null || p.SystemType.IsScalar(false))
					p.IsQueryParameter = false;
			}
		}

		private bool SearchSelectClause(IQueryElement element)
		{
			if (element.ElementType != QueryElementType.SelectClause) return true;

			new QueryVisitor().VisitParentFirst(element, SetNonQueryParameterInSelectClause);

			return false;
		}

		private bool SetNonQueryParameterInSelectClause(IQueryElement element)
		{
			if (element.ElementType == QueryElementType.SqlParameter)
			{
				var p = (SqlParameter)element;
				if (p.SystemType == null || p.SystemType.IsScalar(false))
					p.IsQueryParameter = false;
				return false;
			}

			if (element.ElementType == QueryElementType.SqlQuery)
			{
				new QueryVisitor().VisitParentFirst(element, SearchSelectClause);
				return false;
			}

			return true;
		}

		public override SqlStatement Finalize(SqlStatement statement)
		{
			CheckAliases(statement, int.MaxValue);

			new QueryVisitor().VisitParentFirst(statement, SearchSelectClause);

			if (statement.QueryType == QueryType.InsertOrUpdate)
			{
				var insertOrUpdate = (SqlInsertOrUpdateStatement)statement;
				foreach (var key in insertOrUpdate.Insert.Items)
					new QueryVisitor().Visit(key.Expression, SetNonQueryParameter);

				foreach (var key in insertOrUpdate.Update.Items)
					new QueryVisitor().Visit(key.Expression, SetNonQueryParameter);

				foreach (var key in insertOrUpdate.Update.Keys)
					new QueryVisitor().Visit(key.Expression, SetNonQueryParameter);
			}

			statement = base.Finalize(statement);

			if (FirebirdConfiguration.ConvertInnerJoinsToLeftJoins)
			{
				ConvertInnerJoinsToLeftJoins(statement);
			}

			switch (statement.QueryType)
			{
				case QueryType.Delete : return GetAlternativeDelete((SqlDeleteStatement)statement);
				case QueryType.Update : return GetAlternativeUpdate((SqlUpdateStatement)statement);
				default               : return statement;
			}
		}

		private void ConvertInnerJoinsToLeftJoins(SqlStatement statement)
		{
			statement.WalkQueries(query =>
			{
				new QueryVisitor().Visit(query, e =>
				{
					if (e.ElementType != QueryElementType.SqlQuery)
						return;

					var q = (SelectQuery)e;

					foreach (var join in q.From.Tables.SelectMany(t => t.Joins))
					{
						if (join.JoinType == JoinType.Inner)
						{
							var keys = join.Table.Source.GetKeys(true);

							var useOr       = true;
							var notNullable = keys.Where(w => e is SqlField field && !field.CanBeNull).ToList();
							if (notNullable.Count > 0)
							{
								useOr = false;
								keys  = notNullable;
							}

							join.JoinType  = JoinType.Left;
							var conditions = keys.Select(f => new SqlCondition(false, new SqlPredicate.IsNull(f, true), useOr));
							var sc         = new SqlSearchCondition(conditions);

							q.Select.Where.ConcatSearchCondition(sc);
						}
					}
				});
				return query;
			});
		}

		public override ISqlExpression ConvertExpression(ISqlExpression expr)
		{
			expr = base.ConvertExpression(expr);

			if (expr is SqlBinaryExpression)
			{
				SqlBinaryExpression be = (SqlBinaryExpression)expr;

				switch (be.Operation)
				{
					case "%": return new SqlFunction(be.SystemType, "Mod",     be.Expr1, be.Expr2);
					case "&": return new SqlFunction(be.SystemType, "Bin_And", be.Expr1, be.Expr2);
					case "|": return new SqlFunction(be.SystemType, "Bin_Or",  be.Expr1, be.Expr2);
					case "^": return new SqlFunction(be.SystemType, "Bin_Xor", be.Expr1, be.Expr2);
					case "+": return be.SystemType == typeof(string)? new SqlBinaryExpression(be.SystemType, be.Expr1, "||", be.Expr2, be.Precedence): expr;
				}
			}
			else if (expr is SqlFunction)
			{
				SqlFunction func = (SqlFunction)expr;

				switch (func.Name)
				{
					case "Convert" :
						if (func.SystemType.ToUnderlying() == typeof(bool))
						{
							ISqlExpression ex = AlternativeConvertToBoolean(func, 1);
							if (ex != null)
								return ex;
						}

						return new SqlExpression(func.SystemType, "Cast({0} as {1})", Precedence.Primary, FloorBeforeConvert(func), func.Parameters[0]);

					case "DateAdd" :
						switch ((Sql.DateParts)((SqlValue)func.Parameters[0]).Value)
						{
							case Sql.DateParts.Quarter  :
								return new SqlFunction(func.SystemType, func.Name, new SqlValue(Sql.DateParts.Month), Mul(func.Parameters[1], 3), func.Parameters[2]);
							case Sql.DateParts.DayOfYear:
							case Sql.DateParts.WeekDay:
								return new SqlFunction(func.SystemType, func.Name, new SqlValue(Sql.DateParts.Day),   func.Parameters[1],         func.Parameters[2]);
							case Sql.DateParts.Week     :
								return new SqlFunction(func.SystemType, func.Name, new SqlValue(Sql.DateParts.Day),   Mul(func.Parameters[1], 7), func.Parameters[2]);
						}

						break;
				}
			}
			else if (expr is SqlExpression)
			{
				SqlExpression e = (SqlExpression)expr;

				if (e.Expr.StartsWith("Cast(Floor(Extract(Quarter"))
					return Inc(Div(Dec(new SqlExpression(e.SystemType, "Extract(Month from {0})", e.Parameters)), 3));

				if (e.Expr.StartsWith("Cast(Floor(Extract(YearDay"))
					return Inc(new SqlExpression(e.SystemType, e.Expr.Replace("Extract(YearDay", "Extract(yearDay"), e.Parameters));

				if (e.Expr.StartsWith("Cast(Floor(Extract(WeekDay"))
					return Inc(new SqlExpression(e.SystemType, e.Expr.Replace("Extract(WeekDay", "Extract(weekDay"), e.Parameters));
			}

			return expr;
		}

	}
}
