using SapOdooMiddleware.ItemProvisioning;

namespace SapOdooMiddleware.Tests.ItemProvisioning;

public class LubesCategoryRulesTests
{
    [Theory]
    [InlineData("__import__.lm_cat_additives__diesel_fuel_additives", "additives")]
    [InlineData("__import__.lm_cat_gear_oils_transmission_fluids__atf_automatic_transmission_fluid", "gear_oils_transmission_fluids")]
    [InlineData("__import__.lm_cat_workshop_pro_line", "workshop_pro_line")]
    [InlineData("__export__.product_category_185_041a5e11", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FamilyKey_ReadsTheFamilyOfALiquiMolyCategory(string? externalId, string? expected)
        => Assert.Equal(expected, LubesCategoryRules.FamilyKey(externalId));

    [Theory]
    [InlineData("__import__.lm_cat_additives__diesel_fuel_additives")]
    [InlineData("__import__.lm_cat_workshop_pro_line__additives")]        // Pro-Line's own "Additives" leaf is precise
    [InlineData("__import__.lm_cat_service_products__coolant_antifreeze")]
    [InlineData("__import__.lm_cat_marine__marine_oils")]
    public void CategoryProblem_AcceptsPreciseSubCategories(string externalId)
        => Assert.Null(LubesCategoryRules.CategoryProblem(externalId));

    [Theory]
    [InlineData("__import__.lm_cat_additives__additives", "catch-all")]
    [InlineData("__import__.lm_cat_additives", "whole family")]
    [InlineData("__import__.lm_cat_additives__", "no sub-category")]
    [InlineData("__export__.product_category_183_73e26ee2", "not a LIQUI MOLY category")]
    [InlineData("product.product_category_goods", "not a LIQUI MOLY category")]
    [InlineData("", "no Odoo category")]
    [InlineData(null, "no Odoo category")]
    public void CategoryProblem_RejectsFamiliesCatchAllsAndForeignCategories(string? externalId, string reason)
    {
        var problem = LubesCategoryRules.CategoryProblem(externalId);
        Assert.NotNull(problem);
        Assert.Contains(reason, problem);
    }

    [Theory]
    [InlineData("__import__.lm_cat_workshop_pro_line__diesel_fuel_additives", 112)]
    [InlineData("__import__.lm_cat_service_products__coolant_antifreeze", 120)]  // Layer-1 coolant rule
    [InlineData("__import__.lm_cat_service_products__coolant_antifreeze", 104)]  // older items in 104
    [InlineData("__import__.lm_cat_service_products__brake_fluid", 120)]
    [InlineData("__import__.lm_cat_engine_oils__motorbike_engine_oils", 110)]  // Layer-1 motorbike rule
    [InlineData("__import__.lm_cat_engine_oils__commercial_vehicle_engine_oils", 106)]
    [InlineData("__import__.lm_cat_greases__multi_purpose_grease", 107)]
    [InlineData("__import__.lm_cat_greases__multi_purpose_grease", 117)]
    [InlineData("__import__.lm_cat_accessories__workshop_accessories", 999)]   // family not cross-checked
    [InlineData("__import__.lm_cat_additives__diesel_fuel_additives", null)]   // no group yet
    public void GroupMismatch_NoneWhenCategoryAndGroupFit(string externalId, int? group)
        => Assert.Null(LubesCategoryRules.GroupMismatch(externalId, group));

    [Theory]
    [InlineData("__import__.lm_cat_additives__diesel_fuel_additives", 112)]   // Pro-Line item with an Additives category
    [InlineData("__import__.lm_cat_gear_oils_transmission_fluids__gear_oils", 117)]
    [InlineData("__import__.lm_cat_vehicle_care__interior_care", 109)]
    public void GroupMismatch_ReportedWhenTheyDisagree(string externalId, int group)
    {
        var mismatch = LubesCategoryRules.GroupMismatch(externalId, group);
        Assert.NotNull(mismatch);
        Assert.Contains(group.ToString(), mismatch);
    }

    [Theory]
    [InlineData("Additives", "Diesel additives", "Super Diesel Additive", "Additives / Diesel additives")]
    [InlineData("Additives", null, "Pro-Line Diesel System Cleaner", "Workshop Pro-Line")]
    [InlineData("Additives", "Diesel additives", "Pro-Line Super Diesel Additiv", "Workshop Pro-Line / Diesel additives")]
    [InlineData("Additives", "Additives", "Octane Booster", "Additives")]
    [InlineData("Engine Oils", null, "Top Tec 4200 5W-30", "Engine Oils")]
    [InlineData(null, "Motor Oils", "Special Tec F", "Motor Oils")]
    [InlineData(null, null, "Something", null)]
    public void BuildCategoryHint_AddsSubCategoryAndProLine(string? category, string? sub, string name, string? expected)
        => Assert.Equal(expected, LubesCategoryRules.BuildCategoryHint(category, sub, name));
}
