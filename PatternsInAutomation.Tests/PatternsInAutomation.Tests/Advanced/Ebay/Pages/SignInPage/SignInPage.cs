﻿using PatternsInAutomation.Tests.Advanced.Core;

namespace PatternsInAutomation.Tests.Advanced.Ebay.Pages.SignInPage
{
    public class SignInPage : BasePage<SignInPageMap, SignInPageValidator>
    {
        public SignInPage()
            : base(@"http://www.ebay.com/")
        {
        }

        public void ClickContinueAsGuestButton()
        {
            this.Map.ContinueAsGuestButton.Click();
        }

    }
}
