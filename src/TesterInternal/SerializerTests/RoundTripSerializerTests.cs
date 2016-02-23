﻿using System;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.SerializerTests
{
    public class RoundTripSerializerTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("Serialization")]
        public void Serialize_TestMethodResultEnum()
        {
            var grain = GrainClient.GrainFactory.GetGrain<IEnumResultGrain>(GetRandomGrainId());
            try
            {
                CampaignEnemyTestType result = grain.GetEnemyType().Result;
                Assert.AreEqual(CampaignEnemyTestType.Enemy2, result, "Enum return value wasn't transmitted properly");
            }
            catch (Exception exception)
            {
                Assert.Fail("Call to grain method with enum return threw exception: " + exception);
            }
        }
    }
}
