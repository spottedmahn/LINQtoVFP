using System.Linq;
using System.Linq.Expressions;
using IQToolkit;
using IQToolkit.Data.Common;

namespace LinqToVfp.ExpressionRewriters
{
    internal class TakeRewriter : VfpExpressionVisitor
    {
        private readonly VfpLanguage _language;

        private TakeRewriter(VfpLanguage language)
        {
            _language = language;
        }

        public static Expression Rewrite(Expression expression)
        {
            var rewriter = new TakeRewriter(VfpLanguage.Default);

            expression = rewriter.Visit(expression);

            return expression;
        }

        protected override Expression VisitSelect(SelectExpression select)
        {
            var takeExpression = select..Take;

            if(takeExpression != null)
            {

            }

            return base.VisitSelect(select);
        }
    }
}
