/* fptest: checks the libm functions that RayMarch Studio Benchmark uses
 * for the glass (issue #1). Prints the computed and the expected values. */
#include <stdio.h>
#include <math.h>

static int bad = 0;

static void check(const char *what, double got, double want)
{
    double e = fabs(got - want) / (fabs(want) > 1e-300 ? fabs(want) : 1);
    int ok = e < 1e-12;
    if (!ok) bad++;
    printf("%-22s %.17g  expected %.17g  %s\n", what, got, want, ok ? "ok" : "WRONG");
}

int main(void)
{
    volatile double a = 0.85, b = 0.37, c = 0.22, d = 1.3, e = 0.9, f = 128, g = 0.001, h = 0.05;
    printf("fptest 1.0 (RayMarch Studio Benchmark, issue #1)\n");
    check("pow(0.85, 0.37)", pow(a, b), 0.94164022518436097);
    check("pow(0.22, 1.3)", pow(c, d), 0.13968490766928174);
    check("pow(0.001, 0.05)", pow(g, h), 0.70794578438413791);
    check("pow(0.9, 128)", pow(e, f), 1.3900845237714517e-06);
    check("exp(log(0.85) * 0.37)", exp(log(a) * b), 0.94164022518436097);
    check("log(0.85)", log(a), -0.16251892949777494);
    check("exp(-0.37)", exp(-b), 0.6907343306373547);
    check("sqrt(1.3)", sqrt(d), 1.1401754250991380);
    printf(bad ? "FPTEST: %d WRONG\n" : "FPTEST: all ok\n", bad);
    return bad != 0;
}
